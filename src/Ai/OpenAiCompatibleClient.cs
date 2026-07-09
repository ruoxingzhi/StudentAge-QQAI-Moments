using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StudentAge.QQAIMoments.Config;
using StudentAge.QQAIMoments.Models;
using UnityEngine.Networking;

namespace StudentAge.QQAIMoments.Ai
{
    internal sealed class OpenAiCompatibleClient : IAiClient
    {
        private enum ApiEndpointKind
        {
            Responses,
            ChatCompletions
        }

        private sealed class ApiEndpoint
        {
            internal string Url;
            internal ApiEndpointKind Kind;
        }

        private readonly PluginConfig config;

        internal OpenAiCompatibleClient(PluginConfig config)
        {
            this.config = config;
        }

        public IEnumerator Generate(AiPrompt prompt, Action<AiResult> callback)
        {
            if (!config.UseAi.Value || string.IsNullOrEmpty(config.BaseUrl.Value))
            {
                callback(AiResult.Fail("AI disabled or BaseUrl empty."));
                yield break;
            }

            List<ApiEndpoint> endpoints = ResolveEndpoints(config.BaseUrl.Value);
            AiResult lastResult = null;
            int requestAttempts = 0;
            for (int i = 0; i < endpoints.Count; i++)
            {
                ApiEndpoint endpoint = endpoints[i];
                AiResult attempt = null;
                yield return SendOnce(prompt, endpoint, r => attempt = r);
                requestAttempts++;
                if (attempt != null)
                {
                    attempt.RequestAttempts = requestAttempts;
                }
                if (attempt != null && attempt.Success)
                {
                    callback(attempt);
                    yield break;
                }

                lastResult = MergeFailure(lastResult, attempt, endpoint, i);
                if (lastResult != null)
                {
                    lastResult.RequestAttempts = requestAttempts;
                }
                if (!ShouldTryAlternate(attempt, i, endpoints.Count))
                {
                    break;
                }
            }

            callback(lastResult ?? AiResult.Fail("AI request failed before sending."));
        }

        private IEnumerator SendOnce(AiPrompt prompt, ApiEndpoint endpoint, Action<AiResult> callback)
        {
            string body = BuildBody(prompt, endpoint.Kind);
            byte[] payload = Encoding.UTF8.GetBytes(body);
            using (UnityWebRequest request = new UnityWebRequest(endpoint.Url, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(payload);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = Math.Max(1, config.TimeoutSeconds.Value);
                request.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrEmpty(config.ApiKey.Value))
                {
                    request.SetRequestHeader("Authorization", "Bearer " + config.ApiKey.Value);
                }

                yield return request.SendWebRequest();

                bool error = request.result == UnityWebRequest.Result.ConnectionError
                             || request.result == UnityWebRequest.Result.ProtocolError
                             || request.result == UnityWebRequest.Result.DataProcessingError;
                if (error)
                {
                    AiResult fail = AiResult.Fail("HTTP " + request.responseCode + " " + request.error + " " + Short(request.downloadHandler.text, 300) + " endpoint=" + endpoint.Url + " api=" + endpoint.Kind, request.downloadHandler.text);
                    fail.HttpStatus = request.responseCode;
                    fail.ApiEndpoint = endpoint.Url;
                    callback(fail);
                    yield break;
                }

                try
                {
                    string responseBody = request.downloadHandler.text;
                    string rawText = ParseText(responseBody);
                    AiResult parsed = AiStructuredOutputParser.Parse(rawText);
                    if (parsed.HasShouldReply && !parsed.ShouldReply)
                    {
                        AiResult silent = AiResult.Ok(string.Empty, parsed.Actions, rawText, responseBody);
                        silent.HasShouldReply = true;
                        silent.ShouldReply = false;
                        silent.HttpStatus = request.responseCode;
                        silent.ApiEndpoint = endpoint.Url;
                        callback(silent);
                        yield break;
                    }

                    string text = TextSanitizer.Clean(parsed.Text, 80);
                    if (string.IsNullOrEmpty(text))
                    {
                        AiResult fail = AiResult.Fail("AI returned empty text. endpoint=" + endpoint.Url + " api=" + endpoint.Kind, responseBody);
                        fail.HttpStatus = request.responseCode;
                        fail.ApiEndpoint = endpoint.Url;
                        callback(fail);
                    }
                    else
                    {
                        AiResult ok = AiResult.Ok(text, parsed.Actions, rawText, responseBody);
                        ok.HttpStatus = request.responseCode;
                        ok.ApiEndpoint = endpoint.Url;
                        callback(ok);
                    }
                }
                catch (Exception ex)
                {
                    AiResult fail = AiResult.Fail("parse failed: " + ex.Message + " raw=" + Short(request.downloadHandler.text, 300) + " endpoint=" + endpoint.Url + " api=" + endpoint.Kind, request.downloadHandler.text);
                    fail.HttpStatus = request.responseCode;
                    fail.ApiEndpoint = endpoint.Url;
                    callback(fail);
                }
            }
        }

        private bool ShouldTryAlternate(AiResult attempt, int index, int total)
        {
            if (!config.RetryAlternateEndpoint.Value || index >= total - 1)
            {
                return false;
            }
            if (attempt == null)
            {
                return true;
            }

            long status = attempt.HttpStatus;
            if (status == 401 || status == 403 || status == 429)
            {
                return false;
            }
            return status == 0 || status == 400 || status == 404 || status == 405 || status == 415 || status == 422;
        }

        private static AiResult MergeFailure(AiResult previous, AiResult current, ApiEndpoint endpoint, int index)
        {
            if (current == null)
            {
                current = AiResult.Fail("No response from " + endpoint.Url);
                current.ApiEndpoint = endpoint.Url;
            }
            if (previous == null)
            {
                if (index == 0)
                {
                    return current;
                }
                current.Error = "alternate API failed: " + current.Error;
                return current;
            }
            string left = previous.Error ?? "";
            string right = current.Error ?? "";
            current.Error = left + " | alternate failed: " + right;
            return current;
        }

        private static List<ApiEndpoint> ResolveEndpoints(string baseUrl)
        {
            List<ApiEndpoint> endpoints = new List<ApiEndpoint>();
            ApiEndpoint primary = ResolveEndpoint(baseUrl);
            endpoints.Add(primary);
            ApiEndpoint alternate = ResolveAlternateEndpoint(baseUrl, primary);
            if (alternate != null && !string.Equals(alternate.Url, primary.Url, StringComparison.OrdinalIgnoreCase))
            {
                endpoints.Add(alternate);
            }
            return endpoints;
        }

        private static ApiEndpoint ResolveEndpoint(string baseUrl)
        {
            string value = (baseUrl ?? "").Trim();
            if (value.Length == 0)
            {
                return new ApiEndpoint { Url = value, Kind = ApiEndpointKind.Responses };
            }
            string lower = value.ToLowerInvariant();
            string lowerNoSlash = lower.TrimEnd('/');
            string valueNoSlash = value.TrimEnd('/');
            if (lowerNoSlash.EndsWith("/responses") || lower.Contains("/responses?"))
            {
                return new ApiEndpoint { Url = value, Kind = ApiEndpointKind.Responses };
            }
            int chatQueryIdx = lower.IndexOf("/chat/completions?", StringComparison.Ordinal);
            if (chatQueryIdx >= 0)
            {
                return new ApiEndpoint { Url = value, Kind = ApiEndpointKind.ChatCompletions };
            }
            if (lowerNoSlash.EndsWith("/chat/completions"))
            {
                return new ApiEndpoint { Url = valueNoSlash, Kind = ApiEndpointKind.ChatCompletions };
            }
            if (lowerNoSlash.EndsWith("/v1"))
            {
                return new ApiEndpoint { Url = valueNoSlash + "/chat/completions", Kind = ApiEndpointKind.ChatCompletions };
            }
            if (!value.EndsWith("/"))
            {
                value += "/";
            }
            return new ApiEndpoint { Url = value + "v1/chat/completions", Kind = ApiEndpointKind.ChatCompletions };
        }

        private static ApiEndpoint ResolveAlternateEndpoint(string baseUrl, ApiEndpoint primary)
        {
            if (primary == null || string.IsNullOrEmpty(primary.Url))
            {
                return null;
            }

            string value = (baseUrl ?? "").Trim();
            if (value.IndexOf("?", StringComparison.Ordinal) >= 0)
            {
                return null;
            }
            string root = value.TrimEnd('/');
            string lower = root.ToLowerInvariant();
            if (lower.EndsWith("/chat/completions"))
            {
                root = root.Substring(0, root.Length - "/chat/completions".Length);
            }
            else if (lower.EndsWith("/responses"))
            {
                root = root.Substring(0, root.Length - "/responses".Length);
            }
            else if (!lower.EndsWith("/v1"))
            {
                root = root + "/v1";
            }

            if (primary.Kind == ApiEndpointKind.ChatCompletions)
            {
                return new ApiEndpoint { Url = root.TrimEnd('/') + "/responses", Kind = ApiEndpointKind.Responses };
            }
            return new ApiEndpoint { Url = root.TrimEnd('/') + "/chat/completions", Kind = ApiEndpointKind.ChatCompletions };
        }

        private static string Short(string value, int max)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }
            value = value.Replace("\r", " ").Replace("\n", " ");
            return value.Length <= max ? value : value.Substring(0, max);
        }

        private string BuildBody(AiPrompt prompt, ApiEndpointKind kind)
        {
            return kind == ApiEndpointKind.ChatCompletions ? BuildChatCompletionsBody(prompt) : BuildResponsesBody(prompt);
        }

        private string BuildResponsesBody(AiPrompt prompt)
        {
            JObject root = new JObject();
            root["model"] = config.Model.Value;
            root["temperature"] = prompt.Temperature;
            root["max_output_tokens"] = prompt.MaxTokens;
            if (config.StructuredOutputEnabled.Value && config.UseJsonResponseFormat.Value)
            {
                root["text"] = new JObject
                {
                    ["format"] = new JObject
                    {
                        ["type"] = "json_object"
                    }
                };
            }

            JArray input = new JArray();
            input.Add(new JObject
            {
                ["role"] = "system",
                ["content"] = prompt.System
            });
            input.Add(new JObject
            {
                ["role"] = "user",
                ["content"] = prompt.User
            });
            root["input"] = input;
            return root.ToString(Formatting.None);
        }

        private string BuildChatCompletionsBody(AiPrompt prompt)
        {
            JObject root = new JObject();
            root["model"] = config.Model.Value;
            root["temperature"] = prompt.Temperature;
            root["max_tokens"] = prompt.MaxTokens;
            if (config.StructuredOutputEnabled.Value && config.UseJsonResponseFormat.Value)
            {
                root["response_format"] = new JObject
                {
                    ["type"] = "json_object"
                };
            }

            JArray messages = new JArray();
            messages.Add(new JObject
            {
                ["role"] = "system",
                ["content"] = prompt.System
            });
            messages.Add(new JObject
            {
                ["role"] = "user",
                ["content"] = prompt.User
            });
            root["messages"] = messages;
            return root.ToString(Formatting.None);
        }

        private static string ParseText(string json)
        {
            JObject root = JObject.Parse(json);
            JToken content = root["output_text"];
            if (content != null && content.Type != JTokenType.Null)
            {
                return content.ToString();
            }

            content = root.SelectToken("output[0].content[0].text");
            if (content != null && content.Type != JTokenType.Null)
            {
                return content.ToString();
            }

            JToken output = root["output"];
            if (output != null && output.Type == JTokenType.Array)
            {
                foreach (JToken item in output)
                {
                    JToken itemContent = item["content"];
                    if (itemContent == null || itemContent.Type != JTokenType.Array)
                    {
                        continue;
                    }
                    foreach (JToken part in itemContent)
                    {
                        JToken text = part["text"];
                        if (text != null && text.Type != JTokenType.Null)
                        {
                            return text.ToString();
                        }
                    }
                }
            }

            content = root.SelectToken("choices[0].message.content");
            if (content == null)
            {
                content = root.SelectToken("choices[0].text");
            }
            return content == null ? string.Empty : content.ToString();
        }
    }
}

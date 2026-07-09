using System.Collections;
using System;
using StudentAge.QQAIMoments.Models;

namespace StudentAge.QQAIMoments.Ai
{
    internal interface IAiClient
    {
        IEnumerator Generate(AiPrompt prompt, Action<AiResult> callback);
    }
}


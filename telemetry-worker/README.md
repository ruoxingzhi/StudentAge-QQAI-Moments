# StudentAge QQ AI Telemetry Worker

这是给 `StudentAge.QQAIMoments` 插件使用的可选 Cloudflare Workers + D1 数据接收端。

插件默认不会上传任何数据。只有玩家在游戏内明确同意共享，并且本地配置填写了 `ShareUsageDataEndpoint` 后，才会向这个 Worker 发送数据。

## 接口

- `POST /ingest?token=...`：接收插件上报的 JSON。
- `GET /health`：检查服务是否在线。
- `GET /stats?admin_token=...`：查看简要统计。
- `GET /export?admin_token=...&limit=100`：导出最近数据，方便分析 NPC 回复质量。

默认不记录真实 IP，只保存 IP hash 和 UA hash。完整插件 payload 会保存到 `payload_json`；是否包含玩家原文，取决于玩家在游戏里的二次同意。

## 部署步骤

1. 安装 Node.js，然后进入本目录：

   ```powershell
   cd telemetry-worker
   npm install
   ```

2. 登录 Cloudflare：

   ```powershell
   npx wrangler login
   ```

3. 创建 D1 数据库：

   ```powershell
   npx wrangler d1 create qqai-telemetry
   ```

   把输出中的 `database_id` 填到 `wrangler.toml`。

4. 初始化表结构：

   ```powershell
   npx wrangler d1 execute qqai-telemetry --remote --file=./schema.sql
   ```

5. 设置两个密钥：

   ```powershell
   npx wrangler secret put INGEST_TOKEN
   npx wrangler secret put ADMIN_TOKEN
   ```

   - `INGEST_TOKEN`：插件上报使用。生成一串随机长字符串即可。
   - `ADMIN_TOKEN`：查看统计/导出使用，不要写进插件源码或公开配置。

6. 部署：

   ```powershell
   npx wrangler deploy
   ```

7. 部署成功后会得到类似地址：

   ```text
   https://studentage-qqai-telemetry.<你的账号>.workers.dev
   ```

   插件配置里填写的上报地址格式：

   ```text
   https://studentage-qqai-telemetry.<你的账号>.workers.dev/ingest?token=你的INGEST_TOKEN
   ```

## 查看数据

```powershell
curl "https://studentage-qqai-telemetry.<你的账号>.workers.dev/stats?admin_token=你的ADMIN_TOKEN"
curl "https://studentage-qqai-telemetry.<你的账号>.workers.dev/export?admin_token=你的ADMIN_TOKEN&limit=50"
```

## 注意

- `INGEST_TOKEN` 一旦写入公开插件配置，就只能作为基础误投防护，不能当强密钥。
- 用户未同意共享时，插件不会上传。
- 用户未同意“原文共享”时，payload 只包含统计、hash 和区间，不包含玩家原文。
- `.dev.vars`、真实 token、Wrangler 本地缓存和 `node_modules/` 不应提交到公开仓库。


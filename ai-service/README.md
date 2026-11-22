# Vchem 智能助教 

 **设计阶段 → AI 助教** ：
读取一次提交的 `plan.json` / `result.json`，解释结果、给分层提示、回答实验问题、生成学习小结。
判定仍由 `Assets/Vchem/Core` 的规则链负责；本服务只做解释，不作修改与评分。

## 快速启动

```powershell
python ai-service/app.py
```

也可以直接双击：

- `ai-service\start.bat`：带控制台窗口，适合演示/排错；
- `ai-service\start-silent.bat`：后台无窗口启动。

> 两个脚本都只在双击时运行，关闭进程即停止服务。

- 服务：`http://127.0.0.1:8000`

默认是 **mock 模式**：不需要 API Key，回答来自受控知识包，确定性、可离线跑。
配置云端模型后自动切到 cloud 模式（失败会回退 mock）。

## 云端模型配置

复制 `ai-service/.env.example` 为 `ai-service/.env`（已被 `.gitignore` 忽略）：

```ini
LLM_BASE_URL=https://api.deepseek.com/v1
LLM_API_KEY=<your-api-key>
LLM_MODEL=deepseek-chat
```

Key 只在后端，Unity 不接触。

## 接口

| 方法 | 路径 | 用途 |
|---|---|---|
| POST | `/v1/explain` | 解释结果：发生了什么 / 为什么 / 下一步 |
| POST | `/v1/hint` | 分层提示（`level=L1/L2/L3`） |
| POST | `/v1/ask` | 回答实验/演示现象问题（可带 `keyframe`） |
| POST | `/v1/report` | 生成学习小结 |
| POST | `/v1/rag` | RAG 检索轨迹：查询 → 命中片段/得分 → KG 联动 → 提示上下文 → 回答 |
| GET | `/v1/kg` | 知识图谱概览：实体数、三元组数、谓词类型 |
| GET | `/v1/kg/entity?name=漏斗&depth=1` | 实体子图：节点、边、关联知识片段（别名可用） |
| GET | `/v1/kg/query?name=木条&relation=用于验满` | 按关系过滤三元组 |
| GET | `/v1/demo/current` | 读取 `.workbuddy/artifacts/runs/current/` 最近提交 |
| GET | `/health` | 服务与知识包状态（含 KG 统计） |

请求体：

```json
{
  "plan": { "topology": { "nodes": [], "edges": [] }, "feeds": {}, "verification": {} },
  "result": { "class": "R-COL-01", "outcome": "fail", "errors": [] },
  "question": "为什么漏斗要插到液面以下？",
  "level": "L2",
  "keyframe": "verify"
}
```

回答：

```json
{
  "answer": "……",
  "hints": ["……"],
  "next_action": "……",
  "citations": [{"id": "k-seal", "source": "教材", "quote": "……"}],
  "safety": "ok",
  "level": "L1",
  "provider": "mock",
  "cached": false
}
```

## 最小 RAG 链路（可检查）

`/v1/rag` 用来展示“检索增强”的中间过程，不调用外部大模型：

```json
POST /v1/rag
{
  "question": "为什么漏斗要插到液面以下？",
  "result": {"class": "R-COL-01", "outcome": "fail", "errors": []},
  "top_k": 3
}
```

返回：

- `query`：由判定类、错误说明、关键帧和学生问题拼成的检索查询；
- `tags`：用于加权的规则标签，如 `R-COL-01`；
- `chunks`：按相关度排序的知识片段，带 `score`、标题、正文、来源；
- `kg_entities` / `kg_hops` / `kg_boost_chunks`：查询识别到的 KG 实体、一跳子图、用于加权的关联片段；
- `prompt_context`：实际注入提示词的知识片段文本；
- `answer` / `citations` / `safety`：同一检索结果进入 mock 生成器后的回答与引用。

## 安全边界

- 服务只监听 loopback；`AI_HOST` 不是 `127.0.0.1` / `::1` / `localhost` 时直接拒绝启动。
- CORS 只允许同源（`http://127.0.0.1:8000` / `http://localhost:8000`）；跨源请求和异常 `Host` 返回 403。
- `/v1/demo/current` 只允许同源读取；API Key 只在本机 `ai-service/.env`，不进入 Unity 或 Git。
- 学生问题按不可信内容处理；超纲/危险问题由安全策略拒答。

## 演示

1. 按上文所述方式启动。
2. Unity M2 提交一个错误方案（例如未液封）。
3. 诊断卡点“问助教” → 解释这次结果 → L1/L2/L3 提示 → 输入“木条为什么会熄灭？”。
4. 打开 `http://127.0.0.1:8000/`，载入最近提交，选关键帧 `turbid` 问“石灰水为什么变浑？”。
5. 点“学习小结”。

## 边界

- 只讨论实验相关问题。
- Unity 侧已接：诊断卡「问助教」；播放器底部「问助教」按当前关键帧提问。

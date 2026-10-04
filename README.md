# PataCoop

PATAPON 1+2 REPLAY（Steam）的非官方模組：讓 2～4 位玩家透過 **Radmin VPN**（或區網）一起玩 **Patapon 2 的故事關卡**。

- 每個人帶自己存檔裡的完整部隊上同一個戰場，打自己的鼓、指揮自己的部隊；部隊各走各的，前進跟單人一樣即時。
- 一位玩家開房（伺服器跑在他的遊戲裡），場上有哪些敵人、敵人的位置和血量、死亡、機關破壞、天氣、過關都以房主為準。
- 人越多敵人越強。
- 每個人的存檔各自記錄過關、戰利品和劇情進度；主線道具保證每人都拿得到。

> 本模組與 Bandai Namco 無關。這個 repo 不含任何遊戲檔案，每位玩家都要有自己的遊戲（Steam 家庭共享也可以）。第三方元件的授權見 [`dist/THIRD-PARTY-NOTICES.txt`](dist/THIRD-PARTY-NOTICES.txt)。

## 玩家：安裝與遊玩

1. 關閉遊戲，下載最新的 `PataCoop-<版本>.zip`（Releases）並解壓縮。
2. 雙擊 `install.bat`，照提示操作（名字直接 Enter＝Steam 名稱；跳出系統管理員確認按「是」）。
   會自動安裝 BepInEx、PataCoop、防火牆規則，沒有 Radmin VPN 也會幫你裝。
3. 跟朋友在同一個 Radmin VPN 網路裡，開遊戲 → Patapon 2 → 營地。
4. 右上角 PataCoop 面板：房主按「開房間」，其他人在房間列表按「加入」。

詳細說明見 [`dist/README.txt`](dist/README.txt)。

**讓 AI 幫你裝**：用 Claude Code、Codex、Cursor 等 AI agent 開這個 repo，跟它說「照 AGENTS.md 幫我設定 PataCoop」。[`AGENTS.md`](AGENTS.md)（`CLAUDE.md` 會自動載入它）裡有給 agent 看的完整步驟。

## 開發

- 需求：Windows + WSL、.NET 8 SDK、遊戲裝好 BepInEx be.785 並啟動過一次（產生 `BepInEx/interop`）。
- 遊戲路徑：設環境變數 `PATACOOP_GAME`，或建立 `Directory.Build.props.user`（見 `Directory.Build.props`）。
- 編譯：`cd plugin && dotnet build -c Release`；打包：`tools/release`。
- 測試工具（同一台電腦開多個遊戲）、架構、遊戲引擎的地雷和修改規則都寫在 [`AGENTS.md`](AGENTS.md)。
- 想一起改：fork 後發 pull request，說明怎麼測過的。

| 資料夾 | 內容 |
|---|---|
| `plugin/` | 模組本體 |
| `server/` | 連線中繼伺服器（也編進模組，房主的遊戲裡跑） |
| `servertest/` | 伺服器測試 |
| `dev/` | 開發用外掛（遊戲內執行程式碼、存檔沙盒），不給玩家 |
| `dist/` | 給玩家的安裝程式與說明 |
| `tools/` | 測試腳本（WSL） |

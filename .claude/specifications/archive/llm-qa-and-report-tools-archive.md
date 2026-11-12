# 本地 LLM Q&A 與資料查詢/報表工具化規劃——已完成部分封存

> 自 [`../llm-qa-and-report-tools.md`](../llm-qa-and-report-tools.md) 於 2026-09-30 抽出。**唯讀參考**：內容為當時紀錄，不再更新；
> 章節編號沿用原檔，現況與未完成項目以原檔與 [`../STATUS.md`](../STATUS.md) 為準。
> ⚠️ 本檔與原檔同屬 `qa-extract-generate.py` 的**紅線**（整個 `archive/` 目錄），不得收錄進 Q&A 語料。

## 修訂紀錄


---

> **修訂紀錄**
> - 2026-08-01 初版
> - 2026-08-03 依程式碼查證修正：§七 排程基礎設施事實更正（專案無 Hangfire）、
>   §5.2 新增相對期間（`RelativePeriod`）、新增 §八「資料權限與機密資料邊界」、
>   新增 §十「Phase 1 RAG 設計細節」與 §十一「Phase 2 工具選取與參數解析」。
>   文中凡標註 `檔案:行號` 者均為 2026-08-03 實際查證結果。
> - 2026-08-03（二）Phase 0 導入影響評估：§8.1 補上「`CanRead` 恆為 `CanCreate` 真超集」的機制證明、
>   量化擴散範圍與既有暴露；§5.5 T2 依 `ApplicationDbContext` 三個 public 上下文屬性改寫；
>   §6.1 新增「特徵測試先於重構」前置；新增 **§十五「Phase 0 導入影響評估與 Core 邊界」**
>   （含需動 Core 部分的完整說明）。
> - 2026-08-03（三）§七 全面展開為 7.0–7.9：**事實更正「專案無任何 Email 基礎設施」**（§7.3）、
>   排程表達力改用受限列舉取代 Cron（§7.2）、新增 `ReportRun` 執行紀錄（§7.4）、
>   檔案落地禁放 `wwwroot`（§7.5）、錯過補跑與多副本去重（§7.6–7.7）、
>   **新增 §7.9「常用查詢」**（＝`ScheduleKind = null` 的同一張 `ReportDefinition`）；
>   連帶把 `RelativePeriod` 的必要性由 Phase 3 提前至 Phase 0-B（§5.2.1）。
> - 2026-08-03（四）**執行前總體複核**，五項查證後的修正：
>   ① §5.5 新增 T6／T7 與「T6 說明」——`IUserContext` 無 HTTP 時靜默回 null ＋ 廠別過濾無 null 旁路
>   ＝ **排程靜默產出空報表**；② 新增 §7.0.1 三個實體決定（`ReportDefinition`/`ReportRun`
>   **刻意不實作** `IPlantEntity`/`IDataAuthEntity`，否則派工器一筆都掃不到、排程全面靜默停擺）；
>   ③ **§8.6(c) 事實更正**：`AuditInterceptor` 是 `SaveChangesInterceptor`，**查詢不產生任何 `AuditLog`**，
>   全量稽核必須自建寫入路徑；④ §三 功能開關拆兩支（Phase 0 零 LLM 依賴，不應綁 `QaAssistant`）；
>   ⑤ **新增 §十六「新增畫面的規範遵循」**——畫面清單 S-1～S-8 與對應 `.claude/commands/` 規格、
>   10 項共通強制項、S-7 對話畫面的例外邊界。待決增至 17 項。
> - 2026-08-03（五）**事實更正：使用手冊已補上英文版**（中英同頁 span 對＋英文截圖 121 張）。
>   §10.1 改寫並補雙語實作機制表；§10.2 新增**第 0 條切塊規則**——雙語同存於一份 HTML，
>   `display:none` 對 `textContent` 無效，直接抽文字會得到中英逐句交錯的字串且**三個環節都不報錯**；
>   §10.3 向量表加 `lang` 欄位；§10.4 改寫（不需模型翻譯、語系須硬過濾、以提問文字判語系）；
>   **待決 #4 解除**（降為實作細節）、待決 #16 因「已是雙語部署」由「只有雙語部署會踩到」升為必踩。
> - 2026-08-04 **§七 六項待決全數裁決**（#1 方案 A／#12 E1／#13 受限列舉／#15 收件人子表／
>   #16 依語系去重各產一份／#14 L3 延後但釘死「以檢視者身分檢權限」），§十四 改為
>   〈已定案〉＋〈尚待決議〉兩張表；**新增 §十七「版控與安裝可選性」**——查證後推翻 §三 兩條原
>   假設：① `Program.cs:476-481` 的 `db.Database.Migrate()` 會套用**全部** `ApplicationDbContext`
>   migration，「pgvector 表只在功能開啟時觸發」在單一 DbContext 下**做不到**，須拆獨立
>   `QaDbContext`＋自己的 `__EFMigrationsHistory`；② `docker-compose.yml` 的 `db` 是
>   `postgres:16-alpine`（**無 pgvector**），且一個 service 只能有一個 image，`profiles:` 解不了，
>   須用 override 檔〔**② 已於 2026-08-05 作廢——改為原生安裝、全面移除 Docker 規劃，見 §17.5**〕。
>   另釘出「啟用開關會經 `RolePermissionSeeder` 3b 自動補鍵**擴散授權**」的陷阱。
> - 2026-08-04（二）**業務裁決 #2／#18／#19 定案並實作，Phase 0-A 主體完成**：
>   ① 機密分級＝含金額（28 支），但**不用動詞表達**——實測發現 MGR 在 13 個模組全是 `Read`、
>   FIN 對 INV 也只有 `Read`，用 `CanCreate` 當機密閘門＝主管與財務看不到任何機密報表；
>   改為動詞統一 `CanRead` ＋ 分級由 `MenuPermission` 資料承載（可見角色＝HQ ∪ MGR ∪ FIN ∪ 該模組 `Full`），
>   並以 `RolePermissionSeeder` 步驟 3c 一次性校正與動詞變更**同批上線**（只改動詞會無痕擴散）。
>   ② #18 維持 `CanReadAny`——欄位級遮罩查證後不可行（`product-detail` 遮成 null 會讓 8 張 INV 單據
>   因前端 `standardCost || 0` 而**靜默寫入成本 0**）。③ #19 維持單筆授權。
>   ④ 新增第三類認定：配方／產能／評比（BOM、Routing、OEE…）不收 `CanRead`（現場要用），
>   改靠既有 `CanPrint` 管出境——**現狀已正確、零改動**，但使待決 #9 更關鍵。
> - 2026-08-04（三）**新增 §17.9「安裝前置條件與已知缺口」**，把三項查證後的新發現定位成檔位前置：
>   ① **專案今天沒有任何檔案備份機制**（`backup-db.sh` 只做 `pg_dump`，連 `wwwroot/uploads` 都不備份）
>   → 報表落地會造成「備份缺口＋保留期形同失效＋還原後 `FilePath` 指向不存在的檔」三連，
>   列為待決 #20 並定為 **P2 安裝前置**（§7.5）；② 待決 #9、#5 升格為 **P3 安裝前置**——
>   #9 未決就開 P3 等於替配方／產能類開設計上的後門；③ 記下 G1（機密分級改由資料承載後，
>   Phase 2 的 L1 目錄過濾會變成每則提問 N 次 `MenuPermissions.AnyAsync`，無快取）、
>   G2（手冊內容過期無測試可抓）、G3（`IsModuleLicensed` 仍是 `=> true` 的樁）。
> - 2026-08-05 **新增 §17.10「具體 git 與安裝作業程序」**，並更新 §17.0 狀態表
>   （Phase 0-A 已於 2026-08-04 主體完成）。§17.10 逐檔查證守門規則後確認：
>   ① **`Modules/` 不在 `validate-boundaries.yml` 的 `PROTECTED` 清單、在漂移 baseline 有 0 筆**，
>   `Data/ApplicationDbContextExtensions.cs` 實測不存在於 `upstream/main`（屬 ProjectZ 新增檔）
>   → **Phase 0／1 全程零 Core 改動、baseline 維持 56、不需要 upstream-first**；
>   ② 因此**不做「有／沒有」兩個發布版本**——分支發布會使 baseline 分岔、漂移守門失效；
>   ③ 揪出一個潛伏缺口：[`check-snapshot-drift.sh:32`](../../../scripts/check-snapshot-drift.sh#L32)
>   呼叫 `has-pending-model-changes` **沒帶 `--context`**，`QaDbContext` 一落地就會因
>   「多於一個 DbContext」非零離開，而腳本把非零一律當成 snapshot 不一致，**錯誤訊息指向 Core 同步**；
>   ④ 記下本機 clone 預設未啟用 `core.hooksPath .githooks`（唯一防線是 CI）。
> - 2026-08-05（二）**全面移除 Docker 規劃（§17.5 改寫）＋新增 §17.11「開發環境資源策略與開源選配準則」**：
>   ① 實測開發機**根本沒有 Docker**（PostgreSQL 是 apt 版 16.14、未裝 pgvector），
>   原 `docker-compose.qa.yml` override 方案等於要求開發者為了驗證 HTTP 呼叫先裝一整套 Docker；
>   改為 **pgvector 走 `apt install postgresql-16-pgvector`（1–2 MB，不動既有 PG 服務）
>   ＋ Ollama 原生安裝綁 `127.0.0.1`**。同時避開兩個原方案的坑：換 `db` 基底映像的風險、
>   容器內 GPU 未顯式宣告會**靜默跑 CPU 且無錯誤訊息**。落差已記錄：
>   **PG 跑在容器內的部署要開 P3 需另行決定**，P1／P2 不受影響。
>   ② §17.11 解決「想測 Q&A 又怕 vhdx 膨脹」：關鍵是 **Ollama 為進程外 HTTP 服務，模型不必落在 WSL 裡**——
>   實測 Windows 端已裝 Ollama 且 models 資料夾為 0 bytes，設 `OLLAMA_MODELS=D:\ollama\models`
>   即 **vhdx 增量 = 0**、`ollama rm` 即時回收；`.wslconfig` 加 `networkingMode=mirrored`
>   可讓 WSL 內 `localhost:11434` 直達且 **Ollama 維持綁 127.0.0.1**（切換後須重跑 UITests 確認 port 行為）。
>   訂出 S1（無模型，驗切塊/語系/權限，磁碟 0）→ S2（1–2B，驗管線）→ S3（8B，僅為裁決 #5）三步計畫，
>   WSL 內全程增量 < 150 MB。另記：`df` 顯示的 942 G 是假的（vhdx 上限 1 TB 但 C: 只剩 184 G），
>   且 **vhdx 只增不減**（現 22.9 G vs 內部實用 15 G，約 8 G 空洞）→ 先跑 `wsl --manage --set-sparse true`。
>   ③ 開源選配定案**維持同組件＋旗標**：`Program.cs:271` 的模組掃描是
>   `AppDomain.CurrentDomain.GetAssemblies()`（**只掃已載入組件**），拆獨立 csproj 會因延遲載入而
>   **靜默不註冊**，真 plugin 模式須改 Core。而 QAA 相依極輕（pgvector EF 套件 ~200 KB、Ollama 走 REST 免套件），
>   拆組件利益 < 1 MB。**觸發拆組件的條件是「重量級／原生相依」（如 ONNX Runtime、llama.cpp binding），不是「有沒有 LLM」。**
> - 2026-08-06 **S1／S2 落地並連帶推翻兩條原假設**（§10.9 落地紀錄、定案 #27–#29）：
>   ① §10.5 把混合檢索的理由寫成「專有名詞檢索」，塊層級實測**否定了這個前提**——
>   純語意 Recall@1 **30/30**、等權 RRF 27/30、純詞彙 18/30，**融合比純語意差**，
>   且**沒有任何一題是詞彙勝過語意的**（含 NCR／OEE／StockShortage 這些原本認為非詞彙不可的案例）
>   → 預設改 `semantic-first`，詞彙通道降為推論服務不可用時的降級路徑；
>   ② 相關度門檻在語意分數上**同樣訂不出來**（正解 min 0.4094 vs 離題 max 0.4785 仍重疊，
>   撐高上界的是「停薪」被比成「停機」的字形假朋友）→ 維持不設硬門檻。
> - 2026-08-06（二）**語料擴充的設計裁決，新增 §10.11「擷取物語料」（定案 #30–#34）**。
>   起點是一個正確的觀察：**手冊回答不了所有問題**（它只講畫面操作，不講規則）。
>   但查證後三件事改變了做法：① `.claude/specifications/` 的 17,331 行裡有約 **100 處
>   「已被推翻的舊方案」**，用完整句子寫著錯誤答案，語意上與正解一樣像——
>   **本規格自己就是活例子**（§14.2 #5 一路寫著已作廢的 `bge-m3`，本次一併更正）；
>   ② 實跑 `dotnet publish` 清點產物：`.cs` **0** 個、`.md` 僅 2 個（皆為 `wwwroot/lib/` 下的
>   第三方 LICENSE）——**只有 `wwwroot` 底下的東西會走**，執行期掃檔的寫法會讓
>   **開發機正常、正式機索引 0 筆且不報錯**；③ 決定擷取物**開放查詢**後，
>   **安全邊界從執行期權限過濾前移到索引前的內容審核**，人工核可因此成為唯一防線。
>   → `.md` 與程式碼**一律擷取改寫後才進索引**，只有手冊維持原文；
>   語料按「操作 vs 規則」切分而非按權限；stale 直接不給檢索。
>   另補 §10.7 全候選盤點（Ollama 12 顆 embedding，**3 顆中國來源出局**）與兩項實測：
>   **現有 30 題題庫已飽和**（滿分，量不出候選差別，新增待決 #35 難題題庫）、
>   **`context_length: 2048` 是假的**（實測天花板 8,000–12,000 CJK 字，現有語料最長塊 4,012 字未被截斷）。
> - 2026-08-06（三）**收錄清單逐份審核完成，新增 §10.11.8（定案 #37–#39，解除待決 #36）**。
>   審核推翻了自己前一天訂下的前提：原本打算「挑安全的檔案整份收」，
>   但 **`mfg-domain.md` 同時是最有價值和最危險的一份**——它是製造規則的唯一權威，
>   卻在 §26 寫著「不做備料直接開工即可繞過 FPI／即使 `RequiresFpi = true` 只要不點備料就能開工」，
>   是一句可照做的跳過首件檢驗指引。→ **審核單位改為段落**，排除在擷取改寫時執行。
>   判定結果：擷取 11 份／段落級部分 2 份／不收 5 份／紅線 5 份＋1 目錄
>   （紅線含**本規格自己**——推翻 45、破口 24、憑證 42，三項全庫最高）。
>   另記錄一個獨立的失效模式：**未來式**（`mfg-domain.md` 有 104 行講「規劃中／Phase X」，
>   對規則類問答與過期同樣有害），故擷取只取已實作的現況，`roadmap-fin-cst-sub.md` 整份排除。
> - 2026-08-06（四）**難題題庫落地並據以換掉嵌入模型（定案 #40、#41，解除待決 #35）**。
>   舊 30 題題庫在語意通道上是滿分，量不出候選差別，故新建 53 題三類難題
>   （近義干擾 27／需推理 14／跨頁 12）＋離題 12 題。
>   **過程中更正了本規格自己前一節剛訂下的指標**：原本的「分離度」是絕對餘弦差，
>   而各模型的餘弦落點差很多（granite 全擠在 0.60–0.86），**有單位的量不能跨模型比**；
>   改用尺度無關的「有幾題離題分數高過正解下界」之後**排名翻轉**——
>   snowflake 由第三變第一（3/12），granite 由第二變最後（10/12）。
>   另修掉一個同方向的偏差：正解沒進榜就不入樣本，**模型越差樣本越乾淨**（granite 只有 69/72）。
>   四顆實測後 `snowflake-arctic-embed2` 四項贏三項，且**不加前綴仍贏過加了官方前綴的
>   `embeddinggemma`**（採用前補做的 A/B），故換模型。定案 #29「不設硬門檻」維持不變——
>   **四顆模型的離題上界全由同一題產生**（「員工特休假怎麼請」比到「特殊日」），
>   換模型解不掉字形假朋友。
> - 2026-08-07 S3 前半：新增 §10.8.2「提示詞契約與生成品質評測」。
>   提示詞強制輸出可解析的引文行（`來源：頁面代號`），因為沒有固定格式就量不到品質；
>   四項指標刻意兩兩成對（引文配平均引用頁數、拒答配誤拒），單看一項都會選錯模型。
>   ~~**待決 #5 的原命題（Breeze vs TAIDE）查證後不成立**——兩者在 Ollama 官方 library 皆為 404~~，
>   （**2026-08-12 更正：404 屬實但推論錯誤，兩顆其實拉得到也已補測，見下方 08-12 條**）
>   候選依「拉得到 × 授權可公開發布 × 非中國來源」重訂，授權逐顆讀 registry 的 license blob。
>   順帶查證：**Gemma 4 已改用 Apache 2.0**（Gemma 3 仍是 Gemma Terms of Use），
>   與 HuggingFace `google/gemma-4-*` 標記一致。
> - 2026-08-12 **補測 Breeze 與 TAIDE（§10.8.2 之十、定案 #46），並更正之四的錯誤推論。**
>   使用者質疑「這兩顆在 ollama 應該可以用」——**質疑成立**：官方 library 確實 404，
>   但 ollama 可直接 `pull hf.co/<repo>:<quant>`，**是我把「拉得到」窄化成「官方 library 有」**，
>   兩者從來不等價，於是連量都沒量就刷掉兩顆。補測結果 `gemma4:latest` 維持不變
>   （Breeze 引文 15/52、TAIDE 10/52 且**離題拒答 0/12——從不說查無**）。
>   ⚠️ 最該記的不是名次：**兩顆繁中缺陷都是 0，但指標早已飽和**（gemma4 系列本來就是 0），
>   在地模型的主場優勢兌現不出來——**為某特性選模型前，先確認該特性的指標還沒飽和**。
>   同段另記一個真缺陷（提示詞範例頁碼被照抄）與一個未解釋但已收斂的異常（檢索分母 51/52）。
> - 2026-08-12 **待決 #9 裁決：NL 回答算 `Read`**（使用者裁決，經 dev-f1 轉述）。
>   §17.9 **P3-a、P3-b 兩項安裝前置至此全部解除**。⚠️ §8.1 那段「NL 是繞過 `CanPrint` 的
>   出境管道」的風險敘述**刻意保留不刪**——裁決不使風險消失，只是接受它。
> - 2026-08-11 新增 §10.9 S2-e ＋ 定案 #45：另一 session 回報「查詢／搜尋」畫面用字不一致
>   會打壞詞彙通道。**用字不一致屬實且更嚴重（同一個 `Search` 鍵兩種中譯），但檢索名次實測不受影響**
>   （兩種問法 top-1／top-3 完全相同，僅分數差 0.04–0.05）→ 不加同義詞層。
>   同段記下一個方法錯誤：第一版量測漏掉生產路徑的 `Lang` 過濾而報出假缺陷。
> - 2026-08-11 **待決 #5 結案：預設生成模型定為 `gemma4:latest`（定案 #44）。**
>   六顆候選全部跑完，它在六項指標上全部最好或並列最好，**沒有一項需要權衡**。
>   最後一顆 `phi4:latest` 因出貨預設的 180 秒逾時而必須放寬到 900 秒才量得到
>   （否則等於用延遲評選，違反 §17.11.3）——**給了它獨有的寬鬆條件，它仍然每一項都輸**。
>   兩個與直覺相反的觀察：同家族更大的 `gemma4:12b` 反而更差（44/52 對 50/52）；
>   而「這輪怎麼比上輪慢這麼多」其實是前三輪毀損的徵兆，當時被當雜訊放過（見之九）。
> - 2026-08-11 §16.3 要求的「對話型畫面契約」已補入 `new-screen-projectz.md` §12，
>   S-7 的先決條件解除。契約裡兩個與直覺相反的點：輸出渲染**預設 `textContent`
>   而不是「sanitize 後 innerHTML」**（預設不渲染標記，就沒有白名單開太寬這個失敗面）；
>   **引文必須由畫面端再過濾一次**，不能假設模型守規矩——幻覺引文在六顆候選實測中出現 0～15 次。
> - 2026-08-11 §10.11.7 原本未決的兩點裁決（定案 #42、#43）：擷取物**沿用 `DocChunk` 加
>   `corpus` 欄位**、擷取採**離線批次產草稿 ＋ 人工逐段核可**。兩點都由既有定案推導
>   （#31 使 `Approved` 成為唯一防線、#37 使核可粒度為段落），非量測結果，出處已註明。
>   代價寫進 §10.11.7：同一張表兩套權限規則（手冊走 MenuKey、擷取物開放）。
> - 2026-08-10 新增 §10.8.2 之九：`phi4` 那一輪連毀兩次的根因查明並修正。
>   肇因不在評測，是 `ManualIndexIntegrationTests` 收窄了 Admin 權限而測試庫名寫死，
>   跨 worktree 共用同一份資料。**前提壞掉時品質指標會變好看而不是變難看**
>   （分母跟著縮小），這是本節最該記住的一件事。三層修正：專屬角色、
>   每 5 題重驗前提、長量測用自己的資料庫。
> - 2026-08-17 **之十二的對照量測跑完，兩項預言都成立**（§10.8.2 之十二實跑段）。
>   代價量到了而且指名得出來：手冊 53 題的「檢索給到正解」52→50，
>   被擠掉的是呆滯庫存與發料成本那兩題——**之九賴以偵測前提污染的那個錨，
>   從此會動**，要偵測污染得改看別的不變量。效果也量到了（規則類作答 5/6→6/6、
>   `rules/*` 被引 13 次）。⚠️ 但「引到正解頁」整欄這一輪不可用：`slug` 在同一段時間
>   從段落序號改成標題路徑雜湊（定案 #48），題庫剛補的 `Expected` 又對不上了，
>   逐則核對後六題其實全部引對。⚠️ 另查出 `RejectedRuleProbes` 那兩題**量不到閘門**
>   ——手冊本來就答得出來，而「探針必須是手冊也沒有的東西」正是它自己註解寫過的尺。
> - 2026-08-17 **S-7 互動驗證完成**（§16.3 的最後一項阻塞解除）：
>   `QaAssistantLiveAskTests` 走完整條 HTTP 路徑真的問一題，引文以「無連結、就地展開」
>   的形式回來（定案 #47）。它斷言原始 JSON 的字面而不是反序列化後的列舉——
>   `kind` 掉回數字時，型別化的讀法會把 `0` 讀成 `Manual`，正好抹掉要抓的東西。
> - 2026-08-17 **S-8 補齊這兩支畫面的 UI 測試**（§16.3.2，8 支全綠 7.5 秒）：
>   提問的 POST 由 Playwright 攔下來回合成 JSON，**一顆模型都不需要**，
>   驗的是三層都碰不到的那一段——委派接不接得上、三種引文各畫成什麼形狀、
>   模型輸出有沒有被當成 HTML。`[QaScreenFact]` 在功能開關關閉時 skip：
>   nightly 刻意不開這個功能，沒有這道開關這幾支會以 404 常態紅，而真因是設定不是缺陷。
> - 2026-08-25 **第三個來源（程式碼）的缺口量測**（§10.11.13，登錄為待決 #50）。
>   §10.11.2 列了三個來源，程式碼那一列**從未執行過**——本次只量缺口、不產語料。
>   1,071 則帶中文的註解單位，外推約 350–550 則是現行語料答不出來的真缺口。
>   **抽樣推翻了一個直覺**：分數最低那一帶 8/8 全是實作細節（SVG `viewBox`、
>   零向量餘弦是 NaN），低分不是「語料答不出來」而是「根本不是業務語言」，
>   真缺口集中在 0.45–0.65。刻意**不訂相關度門檻**（§10.8 已實測那個門檻訂不出來），
>   改分層抽樣後外推，結論因此帶區間——那個區間就是誠實的抽樣誤差。
> - 2026-08-25（二）**第三個來源首次執行**（§10.11.13〈已執行〉，使用者裁決「做」）。
> - 2026-08-25（三）**對帳補 `.cs` 的兩個洞**（§10.11.3 之〈上面這套搬到 `.cs` 之後〉）：
>   改程式碼沒改註解被判未變、改註解被判成刪除再新增；並補 `qa-extract-selftest.py`。
>   第一批 66 支 `.cs` → 193 個註解單位 → **177 則待審**，棄權 30%、失敗 0。
>   兩項硬前置落地：`.cs` 切段單位＝「註解區塊＋宣告行」；來源清單預設拒絕，
>   **且由 CI 再守一次**——Python 那道只擋得住「跑產生器」，手寫 manifest 完全繞得過。
>   實作才浮出來的第三件事：**章節位置也在紅線範圍內**（`HeadingPath` 會印給使用者看，
>   而 `.cs` 的天然標題就是類別與方法名）。實跑換來五個解析器修正，全部是
>   「內容對、格式不對就整則丟掉」，其中一個的後果最大：整支檔案全數棄權會產出
>   `entries: []`，而匯入器對空 entries 是**整批中止**——症狀是正式機所有規則類問題
>   一起答不出來、手冊照常。**新的失敗型態**：註解寫「這樣做會出錯所以不這樣做」，
>   改寫成「系統會出錯」，意思相反而讀起來通順，機器抓不到（9 則／5.1%）。
> - 2026-08-25（四）**第二批 42 支跑完，140 則待審**（§10.11.13〈第二批〉）：
>   PUR 51／ORD 37／簽核 26／權限 26，提示詞一字未改。這一批換來兩件事——
>   ①**判過期的雜湊有三份，其中兩份不去 BOM**：來源檔一有 BOM，那份 manifest
>   從產出的那一刻就永遠過期，審核清單靜默跳過、對帳把每一則都判「已變」。
>   第二批 3 支帶 BOM，22 則一產出就被排除；**第一批一支都沒有，所以完全看不出來**。
>   ②**兩道重複掃描不能互相取代**（來源層 vs 擷取物層），且量測用的
>   `scored.jsonl` 因為全域去重，拿它掃重複**恆為零**——那個零跟真的沒有重複長得一樣。
>   ③去重時再抓到同一族的第三個靜默排除：**退回名單原本把理由欄提到的代號也算退回**，
>   於是「與 `X` 逐字相同，保留後者」的 `X` 當場從審核頁消失。第一版改用**節標題**
>   排除，**同一天就不夠用**（新增一張〈保留但改了文字〉的表，5 則又被吃掉）——
>   最後改成看**表頭有沒有〈退回〉**：節標題是散文，表頭是欄位名，讓判定依賴那個
>   非寫對不可的東西。自我測試 14 → **27 項**。
>   使用者逐則裁決 12 則（退 7、改文字後核可 5），**其餘 123 則整批核可**——
>   第二批**當天審完**，語料 774 段：核可 590、退回 184、待審 0。
>   同輪補上 `scrub()` 清三種確定性殘留（全語料改 9 則）。第 7 條的
>   **反面案例漏洞第三次出現**（`RolePermissionSeeder` 把刻意沒做的權限放寬寫成現況）。

## 一、背景與需求分類

系統若要導入本地開源小模型提供 Q&A/資料查詢能力，實際上是兩種性質完全不同的需求，**不能用同一套機制解決**：

| | 需求 1：操作/流程 Q&A | 需求 2：資料查詢/例行報表 |
|---|---|---|
| 本質 | 檢索文件（RAG） | 呼叫工具（function-calling） |
| 資料來源 | `wwwroot/docs/user` 使用手冊（靜態文字） | 既有報表 Controller 的查詢邏輯（即時資料庫） |
| LLM 的角色 | 生成回答本身 | 只做「選工具＋填參數」的解析，不碰資料 |
| 是否一定要 LLM | 是 | **否**——排程觸發、畫面手動觸發完全不需要 LLM |
| 涵蓋範圍特性 | 連續（語意相似度，模糊地帶存在） | 離散（白名單清單，沒有就是沒有） |
| 機密性 | 低（手冊為公開性質操作說明） | **高**（銷售額、毛利、成本等機密資料）→ 見 §八 |

**核心原則**：LLM 絕不直接生成 SQL、絕不直接碰 DbContext。所有資料存取都必須經過已經寫好、已測試、已套用權限與廠別範圍檢查的既有程式碼路徑——LLM 只負責把自然語言轉成「呼叫哪個工具＋什麼參數」的結構化請求，效果上等同於發起一次內部 API 呼叫，真正執行的是應用程式碼本身。

## 二、整體架構分層

```
┌───────────────────────────────────────────────┐
│  觸發層（三種入口，互不依賴）                        │
│  ① 排程 (背景服務 Cron) ── 零 LLM 依賴，現在就能做   │
│  ② 畫面手動操作（既有下拉/表單）── 現在就能做          │
│  ③ 自然語言 (LLM Planner/function-calling) ── 之後加 │
└───────────────────────────────────────────────┘
                    ↓ 都呼叫同一組
┌───────────────────────────────────────────────┐
│  工具層 IReportTool（Phase 0 主體，純 C#）           │
│  強型別 Request/Response DTO                       │
│  內部處理：廠別範圍 / 權限檢查 / 精度換算 / 資料授權     │
└───────────────────────────────────────────────┘
                    ↓
┌───────────────────────────────────────────────┐
│  資料層（既有 EF Core / PostgreSQL）                 │
└───────────────────────────────────────────────┘
```

需求 1（手冊 Q&A）是另一條獨立管線，只在「生成回答」這一步用到 LLM：

```
使用者提問 → pgvector 混合檢索（語意 + 全文，RRF 融合）
           → 組 prompt（引文 + 問題）→ 本地 LLM 生成回答
           → 附手冊頁連結（HelpDocResolver 既有路徑映射）
```

兩條管線可共用：同一顆本地模型、同一套選用模組部署骨架、同一套權限/選單/多語系框架、同一套稽核 log pipeline。**不共用**：pgvector 索引與 embedding 模型只有需求 1 需要；**權限模型也不共用**（見 §八、§十.4）。

## 三、選用功能（Optional Module）設計

若專案經 GitHub 公開發布，此功能必須是**預設關閉、可選啟用**，避免對不需要此功能的使用者造成負擔：

| 面向 | 做法 |
|---|---|
| 開關 | `appsettings.json` 的 `Features:QaAssistant:Enabled`，關閉時連 Service/背景 Job 都不註冊。原本 `appsettings.json` 沒有 `Features` 區塊（2026-08-03 查證），**2026-08-06 已建立**，鍵與註解見 §17.2 |
| **開關必須拆兩支** | **Phase 0（報表工具＋常用查詢＋排程）不可掛在 `QaAssistant` 開關下**——它零 LLM 依賴、零 GPU 需求，是獨立可用的功能。只想要「例行報表自動產生」的部署不該被迫開啟 LLM 助理。→ `Features:ReportTools:Enabled`（Phase 0）與 `Features:QaAssistant:Enabled`（Phase 1/2）分開，後者可宣告依賴前者 |
| 模型二進位 | 不進版控，`.gitignore` 排除 `/llm-models/`、`*.gguf`（**不可用 `models/`**，見 §17.10.1-C）；提供 `scripts/setup-qa-assistant.sh` 供啟用時下載 |
| 授權 | 公開專案預設模型建議 Apache 2.0 授權（Breeze 本體或 Gemma），TAIDE 授權條款需查證後列為選配替代方案 |
| DB migration | ⚠️ **原寫「拆獨立 migration bundle，只在功能開啟時觸發」——在現行架構下做不到**（`Program.cs` 的 `db.Database.Migrate()` 會套用**全部** `ApplicationDbContext` migration，不看任何開關）。正解是拆**獨立 `QaDbContext`**，見 §十七.3 |
| 降級 | Ollama 未啟動時健康檢查偵測，功能標記「未連線」，不擋主系統開機 |
| 外部相依 | **一律原生安裝，不走 Docker**（2026-08-05 定案，見 §十七.5）：pgvector 對現有 PG 16 `apt install` 加擴充（1–2 MB）；Ollama 原生安裝並綁 `127.0.0.1`。Ollama 是進程外 HTTP 服務，與應用程式部署方式解耦——**模型可以完全不落在專案磁碟上**（§17.11） |
| CI | 真正打 Ollama 的整合測試標記 `[Trait("Category","OptionalQa")]`，預設 CI 不跑，避免逼所有 PR 都要有 GPU |
| 選單/權限 | `ProjectZMenuContributor` 登錄時包一層開關判斷，關閉時選單項目完全不出現。⚠️ 但**開啟的那一刻會觸發 `RolePermissionSeeder` 3b 自動補鍵**，若模組矩陣沒先設好＝一開燈就把 LLM 助理發給所有角色，見 §十七.4 |

> 本章是總覽。**實際落地的完整規劃（版控邊界、開關命名與預設值、DB、CI、外部相依、安裝與關閉路徑）見 §十七**，
> 該章是 2026-08-04 逐項查證現行組態後寫的，並更正了本表兩條原假設。

## 四、模型與硬體資源估算

> ### 🚫 硬性限制：不採用中國來源的模型（2026-08-05 裁決）
>
> **適用於所有模型**——生成模型、embedding 模型、reranker，以及日後任何新增的模型角色。
> 這是採用面的限制，不是技術評比的結果，**不因為某個模型「表現比較好」而放寬**。
>
> 因此**排除**（含但不限於）：`qwen`／`bge-*`（北京智源 BAAI）／`deepseek`／`glm`／
> `internlm`／`baichuan`／`minicpm`／`yi`。
>
> **可用**：Google（`gemma`／`embeddinggemma`）、Meta（`llama`）、Microsoft（`multilingual-e5`）、
> Snowflake（`arctic-embed`）、IBM（`granite`）、Mistral，以及台灣的 Breeze（聯發科）／TAIDE。
>
> ⚠️ **新增任何模型前先確認來源**。模型名稱看不出組織歸屬，
> `bge-m3` 曾在本規格中被定案採用（§10.7 初版）就是這樣發生的。

| 項目 | 估算 |
|---|---|
| 主力 LLM（8B 級，Q4量化，如 Breeze/TAIDE） | 4.5–5 GB |
| Embedding 模型（multilingual-e5 / arctic-embed2） | 1–2.2 GB |
| Ollama 執行環境 | 1–4 GB |
| pgvector 索引（手冊全量文字僅數 MB） | < 0.1 GB |
| **硬碟合計** | 最低 12 GB，建議預留 20 GB |
| 記憶體/GPU（**正式上線**） | 建議 12 GB VRAM GPU + 32 GB RAM，常駐模式（`OLLAMA_KEEP_ALIVE=-1`） |
| 記憶體/GPU（**開發驗證**） | 純 CPU 即可，**開發機標準測試模型＝4B 級** |

> 📌 **上面兩列是不同的東西，不要互相套用。**
> 「正式上線」那列是**交付建議**（客戶端硬體規格）。
> 「開發驗證」那列是**本專案開發機的實況**——2026-08-05 實測：
> i7-13700（16C/24T）、47.6 GB RAM、**僅 Intel UHD 770 內顯**。
> Ollama 的 GPU 後端是 CUDA／ROCm／Metal，Intel 內顯不在其中（IPEX-LLM 之類屬分支非主線），
> 故**開發機一律 CPU 推論**，測試模型定為 **4B 級**（見 §17.11.3）。
>
> ⚠️ **開發機量到的延遲不可當作正式環境的數字回報給客戶**：兩者差的是一整個數量級。
> 反過來也一樣——開發機跑得動 4B，不代表可以據此把正式環境的 GPU 建議刪掉。

> **並發槽與 context 長度的關係**：12 GB VRAM 扣掉 8B Q4（約 5 GB）與 embedding 模型後，餘裕可支撐 2 個平行推論槽，但每槽的 KV cache 隨 context 長度線性膨脹。
> 因此 §十一.1「縮減送進 prompt 的工具 schema 量」不只是準確率議題，**直接換算成並發能力**，兩者是同一個約束的兩面。

> Phase 0（工具層＋排程）完全不需要以上資源，屬純後端開發，可獨立於 LLM 基礎設施先行交付。

> 📌 **上表是「正式上線」的估算，不是開發驗證的門檻。**
> 開發機的實測基準、如何讓模型**完全不落在 WSL 的 vhdx 裡**，
> 以及 S1（0 GB）→ S2（1–2 GB）→ S3（5 GB）的三步驗證計畫，見 **§17.11**。
> 該節的結論是：**驗證 Phase 1／2 的絕大部分工作，WSL 內增量 < 150 MB。**

## 五、Phase 0：工具層契約設計

### 5.1 介面

```csharp
public interface IReportTool<TRequest, TResponse>
    where TRequest : ReportRequestBase
{
    string ToolKey { get; }          // 唯一鍵，如 "SalesRanking"
    string Description { get; }      // 供未來 LLM function-calling schema 使用
    Task<TResponse> ExecuteAsync(TRequest request, ClaimsPrincipal user);
}
```

> ⚠️ **`ClaimsPrincipal` 參數不會改變資料過濾行為**。系統的廠別與資料層級過濾實際上是由
> [`ApplicationDbContext`](../../../Data/ApplicationDbContext.cs) 的 global query filter（`ApplicationDbContext.cs:165-210`）
> 讀取 `CurrentPlantId` / `CurrentRoleId` 生效，而這兩個值來自 HTTP 請求環境。
> 傳入 `ClaimsPrincipal` 只影響工具內部的顯式檢查，**不影響 EF 過濾條件**。
> 排程等無 HTTP 上下文的路徑必須顯式帶入授權範圍——見 §5.5 與 §八.3。

### 5.2 標準 Request 基底

所有工具共用同一組篩選欄位，讓排程設定與未來 LLM 參數解析只需學一種模式：

```csharp
public abstract class ReportRequestBase
{
    public int? PlantId { get; set; }              // null=依使用者權限範圍展開，非任意跨廠

    // ── 期間：相對優先、絕對備援，兩者互斥 ──────────────────
    public RelativePeriod? Period { get; set; }     // 滾動視窗語意（LLM 只能填這個）
    public DateRange? AbsolutePeriod { get; set; }  // 明確起訖（畫面手動查詢用）
    public DateBasis DateBasis { get; set; }        // 日期基準：訂單日 / 出貨日 / 開票日

    public string? GroupBy { get; set; }
    public int? TopN { get; set; }
    public bool UseLocalCurrency { get; set; }      // 對齊既有 AmountLocal 慣例
    public string? SortBy { get; set; }
}

public enum RelativePeriod
{
    Today, Yesterday,
    ThisWeek, LastWeek,
    ThisMonth, LastMonth,
    ThisQuarter, LastQuarter,           // 日曆季
    ThisFiscalPeriod, LastFiscalPeriod, // 會計期間（與日曆季可能不同）
    ThisYear, LastYear,
    Last7Days, Last30Days, Last90Days,
    MonthToDate, QuarterToDate, YearToDate
}
```

### 5.2.1 相對期間的設計理由與解算規則

**這不只是防止小模型算錯日期，而是 Phase 0-B 就成立的必要條件。**

任何「把查詢參數存起來、之後重複使用」的功能都需要滾動視窗語意，而這無法以 `DateRange` 表達：

- **常用查詢（§7.9，Phase 0-B）**：存成絕對日期的「上月銷售排行」，下個月點開還是上個月的資料。
- **排程報表（§七，Phase 0-B）**：存成絕對日期的月報，每個月都寄出同一份七月資料。
- **Phase 3 的「NL 查詢另存為排程」**：同上，且錯得更隱蔽（LLM 當下算出的日期看起來完全合理）。

因此 `RelativePeriod` 是 **Phase 0-B 的前提**，不是 Phase 3 才需要的優化項目。

解算規則（強制）：

1. **LLM 只准輸出 `RelativePeriod` 列舉值**，不得輸出絕對日期；絕對日期僅供畫面手動查詢與匯入既有排程使用。
2. **解算必須走既有 `IDateTimeService`**（專案已有時區感知慣例，見 `SalesRankingController.cs` 的注入），不得使用 `DateTime.Now`——否則「昨天」在 UTC 伺服器上會錯一天。
3. **財務類工具須區分日曆季與會計期間**。系統已有會計期間（AccountingPeriod）基礎設施，FIN/ACT 類報表若沿用日曆季會與帳務口徑不符，須使用 `ThisFiscalPeriod` / `LastFiscalPeriod` 並查表解算。
4. **解算後的絕對區間必須回顯在答覆中**。使用者要能驗證「上個月」被解成什麼，這比參數本身重要。
5. **`DateBasis`（日期基準）與 `RelativePeriod` 正交**。相對期間解決日期算術，但「以訂單日還是出貨日計算」的歧義依然存在，仍須走 §九.2 的回顯確認流程。不要誤以為做完相對期間就沒有時間類歧義。

> 實作提醒：現行報表 Controller 的簽章是弱型別字串日期
> （`SalesRankingController.cs:41` — `public async Task<IActionResult> Index(string? fromDate, string? toDate, ...)`）。
> 抽取為 `IReportTool` 時必須在邊界就轉為強型別，**不得讓字串日期滲入工具契約**。

### 5.3 登錄機制

比照既有 `ProjectZMenuContributor` 模式：各模組寫 `IReportToolRegistrar`，啟動時中央 `ReportToolCatalog` 掃描註冊。每個工具用 Attribute 宣告所需權限（對齊既有 RBAC）與廠別範圍規則，執行時強制檢查——不論呼叫者是排程還是未來的 LLM，都繞不過。

權限 Attribute 的宣告規則見 §八.1（**必須逐一對齊該報表畫面現行的實際檢查，不可套統一慣例**）。

### 5.4 與現有 Controller 的關係（實測結果）

實際檢視 `Modules/ORD/Controllers/SalesRankingController.cs` 發現：查詢邏輯已經是獨立的 `private async Task<List<SalesRankingRowVm>> BuildQueryAsync(...)`，Controller 的 `Index`／`Export` 兩個 action 都呼叫它。**這代表 Phase 0 的主要工作不是重新開發報表邏輯，而是把這類已經存在、但目前是 Controller 私有方法的查詢邏輯，抽成獨立可注入的 Service（實作 `IReportTool`），供 Controller、排程、未來 LLM 三方共用**，不得複製邏輯造成日後行為漂移。

### 5.5 工具實作的強制約束

抽取為 `IReportTool` 的服務必須遵守以下規則，建議做成一致性框架規則（R 系列）守門，否則日後必然漂移：

| # | 約束 | 理由 |
|---|---|---|
| T1 | **不得裸用 `IgnoreQueryFilters`**。若因跨廠彙總需要繞過過濾器，必須改為接受「已授權範圍」參數（`authorizedPlantIds` / `authorizedRolePath`）並自行套用 | `IgnoreQueryFilters` 會**同時**關掉廠別過濾與組織層級資料權限過濾，不是只關廠別。詳見 §八.2 |
| T2 | 不得**隱式**依賴 HTTP 請求環境取得廠別/角色。無 HTTP 上下文時（排程、背景摘要），必須在該 scope 內顯式設定 `ApplicationDbContext` 的 `CurrentPlantId` / `CurrentRoleId` / `CurrentRolePath` 三個屬性後才查詢 | 見下方 T2 補充：這三個屬性是 `public set`，設了 global filter 就正確生效，**不需要改 Core、也不必每支工具自行實作範圍過濾** |
| T3 | 回傳 DTO 不得夾帶未經授權的欄位（例如為了計算方便把成本帶回前端再隱藏） | NL 摘要階段會看到整個 DTO |
| T4 | 金額欄位必須標示幣別與口徑（交易幣/本幣、AVCO/標準成本/實際成本） | 見 §九.3 成本口徑歧義 |
| T5 | 所有執行都必須產生稽核紀錄（呼叫者、工具、參數、回傳筆數） | 既有稽核管線**接不到查詢**，必須自建寫入路徑——見 §8.6(c) 事實更正與 §7.4 `ReportRun` |
| T6 | **工具服務不得注入 `IUserContext`**。所需的廠別/角色/語系一律由 `TRequest` 或執行 scope 顯式帶入 | 見下方 T6 說明：這是排程情境**靜默產出空報表**的來源 |
| T7 | 產出內容（欄位標題、日期/數字格式）不得依賴 `CultureInfo.CurrentUICulture` | 排程無 HTTP 請求 → 取到的是**行程預設語系**而非收件人語系。見 §7.3 |

> **T2 補充（2026-08-03 查證，修正初版寫法）**：初版寫「一律由參數傳入」，實測後改為上述寫法，理由如下。
>
> [`ApplicationDbContext.cs:11-13`](../../../Data/ApplicationDbContext.cs#L11-L13) 的三個上下文屬性是 **public set**：
> ```csharp
> public string? CurrentPlantId  { get; set; }
> public string? CurrentRoleId   { get; set; }
> public string? CurrentRolePath { get; set; }
> ```
> Core 自己的 [`UserPreferenceMiddleware.cs:31-33`](../../../Middleware/UserPreferenceMiddleware.cs#L31-L33) 也只是在請求開始時賦值這三行。
> 因此排程情境只要在 scope 內做同樣三行賦值，global query filter 就會**以與畫面完全相同的邏輯**生效。
>
> 這比「每支工具自行接受 `authorizedPlantIds` / `authorizedRolePath` 並自行 `Where`」更好：
> 過濾邏輯只有一份、不會隨工具數量增加而漂移，且日後 Core 調整過濾器時自動跟上。
>
> 兩點必須注意：
> 1. `CurrentRolePath` 的解析來源是 [`IUserContext.cs:64`](../../../Services/IUserContext.cs#L64)
>    （`ActiveRole.OrganizationUnit.Path`），排程情境沒有 `IUserContext`，須自行以 `OwnerRoleId` 查出同一個值。
> 2. T1（不得裸用 `IgnoreQueryFilters`）**依然成立**。跨廠彙總類工具仍須顯式帶入已授權廠別清單，
>    因為單一 `CurrentPlantId` 無法表達「多廠但非全廠」。

> **T6 說明（2026-08-03 查證，本次新增）**：這是排程情境最容易踩、且**完全不會報錯**的一個坑。
>
> [`UserContext`](../../../Services/IUserContext.cs)（`IUserContext.cs:36-55`）取值路徑是
> `_httpContextAccessor.HttpContext?.User` → 無 HTTP 上下文時**不丟例外**，
> `PlantId` / `ActiveRoleId` / `ActiveRolePath` / `Language` **全部靜默回 `null`**。
>
> 而廠別過濾表達式是 `e.PlantId == CurrentPlantId`（`ApplicationDbContext.cs:170-175`），
> **沒有「`CurrentPlantId` 為 null 就放行」的旁路**。兩者相乘的結果是：
>
> ```
> 排程執行 → IUserContext 全為 null → CurrentPlantId 為 null
>          → 過濾條件變成 e.PlantId == null → 撈到 0 筆
>          → 報表正常產生、正常寄出、內容是空的，沒有任何例外或警告
> ```
>
> 既有報表 Controller **普遍注入 `IUserContext`**（`/new-screen` Step 3-1 把它列為必注入），
> 抽 `BuildQueryAsync` 成 Service 時最自然的動作就是把這個依賴原樣搬過去——**搬過去就中招**。
> 因此 T6 不是風格偏好，是把一個靜默失敗轉成編譯期可稽核的約束（可用一致性規則掃 `IReportTool` 實作的建構子）。

## 六、現有報表資產盤點（依 `management-reports.md` 查證，2026-07-16 全數實作完成）

以下清單摘自現行 `.claude/specifications/management-reports.md`，作為 Phase 0「工具化改造」的候選對象。**這些報表邏輯已存在，Phase 0 不需重新開發運算邏輯，只需抽出成 `IReportTool`**：

| 模組 | 已實作報表（節錄） |
|---|---|
| ORD | O1 訂單逾期跟催、O2 客戶/品項銷售排行、O3 銷售同期比較、O4 訂單交期達成率、O5 退貨率分析、O6 銷售預測 |
| PUR | P1 採購逾期跟催、P2 供應商交期達成率、P3 採購金額比較、P4 採購價格比較、P5 委外進度追蹤 |
| INV | I2 存貨價值分析、I3 缺貨分析、I4 呆滯庫存分析、I5 庫存周轉率、I7 盤差分析 |
| MFG | M1 工單進度、M2 不良率、M4 WIP在製品、M5 稼動率/利用率、M6 生產效率、M7 異常停機、M8 MRP達成率、M9 備料進度追蹤 |
| FIN/RPT/ACT | 應收帳齡分析、應付帳齡分析、毛利分析、損益表、資產負債表、管理儀表板、F3 毛利結構分析、F4 NRV存貨評估、F5 壞帳準備ECL |

已知資料缺口（`management-reports.md` §五）：`LotExpiryDate`（過期庫存）、`SalesRepCode`（業務員績效）、QCR 缺 IQC 交易表（進料品質），這些報表本身尚未實作，不在 Phase 0 工具化範圍內。

> **26 是「畫面數」不是「工具數」**。多數報表本身已參數化（O2 銷售排行同時支援 Customer / Product 兩個 `groupBy` 維度），
> 工具化時應先做參數化盤點，目錄很可能收斂。這會直接影響 §十一.1 是否需要額外的路由層。

### 6.1 建置優先順序

1. **前置作業一（先於工具化）**：釐清各報表畫面的權限動詞不一致問題——見 §八.1。這決定了每張報表的機密等級，工具層的權限宣告要照抄結果。

   **2026-08-05 實測結果**：30 支報表型 Controller 中，23 支已是 `CanRead`（Phase 0-A 收斂的成果），
   **仍有 7 支的 Index 閘門是 `CanCreate`**：

   | 模組 | Controller |
   |---|---|
   | INV | `StockShortageController` |
   | MFG | `DefectRateController`、`WorkOrderProgressController` |
   | ORD | `DeliveryRateController`、`OrderOverdueController`、`ReturnRateController` |
   | PUR | `PurchaseOverdueController` |

   這 7 支的共同點是**都不含金額**，所以不在 Phase 0-A 的 28 支機密報表清單內，因而沒被一起改到。

   ⚠️ **這不是安全漏洞，而且改動方向是「放寬」**。`CanRead` 恆為 `CanCreate` 的真超集，
   目前用 `CanCreate` 只是**比預期更嚴格**（只有讀取權的角色看不到這 7 張報表）。
   改成 `CanRead` 會讓這些角色從看不到變成看得到——那是一次存取範圍的擴大。

   ⛔ **2026-08-05 曾裁決「維持 `CanCreate`」，已於 2026-08-14 推翻**（詳見 §8.1-終）。
   **現行結論：30 支全部 `CanRead`**，工具層照抄後也全是 `CanRead`。
   「改動方向是放寬」這個事實判斷仍然成立——放寬是這次裁決要的結果，不是副作用。
   現況由特徵測試**雙向**鎖住（改回 `CanCreate` 是收緊，同樣會紅燈）。

2. **前置作業二：特徵測試先於重構**。報表畫面的測試覆蓋實測近乎為零（見 §15.3），
   而「把 `BuildQueryAsync` 從 Controller 私有方法搬成注入式 Service」是典型的行為應該不變的重構——
   **但沒有任何測試會告訴你它變了**：報表算錯不會 crash，只會靜默吐出不同金額。
   要動的每張報表都須先補特徵測試（characterization test：先鎖住現況輸出，再重構）。此工時必須計入 Phase 0-B，不可事後補。

   ✅ **零覆蓋的 6 支已完成**（2026-08-05）：
   [`SmartFactory.MES.Tests/ORD/OrdReportCharacterizationTests.cs`](../../../SmartFactory.MES.Tests/ORD/OrdReportCharacterizationTests.cs)（15 條）
   ＋ [`SmartFactory.MES.Tests/INV/InvReportCharacterizationTests.cs`](../../../SmartFactory.MES.Tests/INV/InvReportCharacterizationTests.cs)（15 條），共 30 條全綠。

   涵蓋 SalesRanking／OrderOverdue／ReturnRate／InventoryValuation／StockShortage／SlowMovingInventory。
   每個「會改變輸出的判斷分支」各一條斷言：日期區間是否含端點、Draft／未過帳是否計入、
   篩掉零值列的門檻方向、排序、ViewModel 計算屬性、以及**閘門動詞**。

   > **特徵測試必須自我驗證**。一次就全綠的特徵測試沒有任何意義——它可能根本沒碰到那段邏輯。
   > 本批已做突變測試：把 `QtyOnHand != 0` 改成 `> 0`、`OrderDate <= end` 改成 `< end`、
   > `IdleDays >= threshold` 改成 `>`，重跑後**確實是對應的 4 條失敗**（其餘 26 條照綠），
   > 隨後還原。日後補其他報表的特徵測試時，請比照做完這一步再提交。
3. **優先工具化**：ORD/PUR/INV/MFG 各模組已標「✅」的報表，逐一抽取查詢邏輯為 `IReportTool`，風險最低。
4. **視需求新建**：若既有報表仍無法覆蓋某類提問（例如跨報表的「Top N 排行」若既有 O2 的 groupBy 維度不夠用），才新增工具，且優先做**通用性高、可泛化**的參數化查詢，而非為單一問題客製。
5. **不在 Phase 0 範圍**：§六 列出的資料缺口報表，待欄位補齊、報表本身實作完成後才進入工具化清單。

## 七、排程整合與常用查詢

### 7.0 一張表、兩種用途

`ReportDefinition` 是「工具＋固定參數（＋排程＋輸出通道）」的組合，此路徑**完全不需要 LLM**。
關鍵設計：**排程報表與「常用查詢」是同一張表的兩種狀態**，差別只在 `ScheduleKind` 欄位是不是 null。

| 用途 | `ScheduleKind` | 觸發者 | 使用情境 |
|---|---|---|---|
| **常用查詢（我的查詢）** | `null` | 使用者點擊 | 「每次都要查上個月 A 客戶的銷售排行」→ 存一次，之後一鍵重跑（詳見 §7.9） |
| **例行排程報表** | 有值 | 背景服務 | 「每月 1 號寄上月銷售排行給業務主管」 |

這讓 §十三 Phase 3 的閉環（NL 查詢 →「另存」→ 排程）有了**不依賴 LLM 的 Phase 0 版本**：
使用者先把查詢存成常用查詢，用順了再加上排程，兩步都在 Phase 0 交付。

```csharp
public class ReportDefinition
{
    public int Id { get; set; }
    public string Name { get; set; } = "";            // 使用者取的名字，如「上月銷售排行（含金額）」
    public string OwnerUserId { get; set; } = "";
    public string OwnerRoleId { get; set; } = "";     // 執行時據此解析授權範圍（見 §7.8 權限時效）
    public int? PlantId { get; set; }
    public string ToolKey { get; set; } = "";         // 對應 IReportTool.ToolKey
    public string ParamsJson { get; set; } = "";      // 結構化參數，非 SQL；期間必須存 RelativePeriod（見 §5.2.1、§7.9）

    // ── 排程（null = 純常用查詢，不自動執行）────────────────
    public ReportScheduleKind? ScheduleKind { get; set; } // Daily / Weekly / Monthly（見 §7.2）
    public int? ScheduleHour { get; set; }            // 0–23
    public int? ScheduleMinute { get; set; }          // 0–59
    public int? ScheduleDayOfWeek { get; set; }       // Weekly 用
    public int? ScheduleDayOfMonth { get; set; }      // Monthly 用
    public DateTime? NextRunAt { get; set; }          // UTC，dispatcher 掃這欄
    public bool IsActive { get; set; } = true;        // 權限失效時由 S2 停用

    public string OutputFormat { get; set; } = "";    // Excel / Csv / 畫面（Email 不實作，見 §7.3 定案 E1）
    public ICollection<ReportRecipient> Recipients { get; set; } = [];  // 子表，見 §7.0.1(b) 定案
    public string CreatedByRoleId { get; set; } = ""; // 沿用既有 IDataAuthEntity 慣例
}
```

新增排程報表只需在 `ReportDefinition` 表新增一筆設定，**不需要新增程式碼**。

### 7.0.1 三個實體層面的決定（不決定就會靜默壞掉）

**(a) `ReportDefinition` / `ReportRun` 一律「不」實作 `IPlantEntity` 與 `IDataAuthEntity`——這是刻意的例外。**

派工器必須掃描**全部使用者、全部廠別**的到期定義，但它自己的 scope 只能有一組 `CurrentPlantId` / `CurrentRoleId`。
若這兩個實體納入 global filter，後果是：

```
dispatcher scope 未設上下文 → CurrentPlantId = null
  → 過濾條件 e.PlantId == null（無 null 旁路）
  → 但 ApplyGlobalValues 在新增時已 ??= 填入 PlantId（ApplicationDbContext.cs:235-241）
  → 畫面建立的排程一律有 PlantId → dispatcher 一筆都掃不到
  → 排程「全面靜默不執行」，日誌乾淨、無例外、無警告
```

`IDataAuthEntity` 同理（`CurrentRolePath` 空 → 退化成 `e.CreatedByRoleId == null`）。
唯一的替代方案是在 dispatcher 掃描時 `IgnoreQueryFilters`，那會直接違反 §5.5 T1。

→ 正解：**兩個實體都當普通表**。`PlantId` / `OwnerRoleId` / `CreatedByRoleId` 保留為一般欄位，
只在**執行該筆定義時**設進 scope 的三個上下文屬性（§5.5 T2），權限則由 Controller 顯式 `Where` 收斂。

> ⚠️ 這與 `CLAUDE.md`〈Entity 資料範圍規範〉「作業單據一律實作 `IPlantEntity`」的通則**相反**。
> 必須在實體類別上以註解寫明理由，否則日後有人依通則「修正」它，排程會全面停擺且看不出原因。
> 連帶影響：唯一鍵是 `(OwnerUserId, Name)`（§7.9-5），**不加 `PlantId`**——因為它不是廠別實體。

**(b) `RecipientUserIds` 需要明確的 EF 映射決定。**
全庫實體**沒有任何 `List<string>` 屬性的先例**（僅 VM 有），所以沒有既有慣例可抄。
三選一：`jsonb` 欄位（查詢「我是不是收件人」需 JSON 運算子）、`ValueConverter` 轉逗號字串（無法查詢）、
或子表 `ReportRecipient`（可查、可加「每人各自的語系/格式」）。
→ 因為 §7.8 S3 要求**逐一檢查每個收件人的權限**、S5 要求下載時再檢查，
**建議子表**；否則權限檢查會退化成在記憶體裡展開字串。

> ✅ **2026-08-04 定案：子表 `ReportRecipient`**（待決 #15 結案）。
> 欄位至少 `(DefinitionId, UserId)` 唯一鍵；**並在同一張子表放 `Language`**——
> 這正好承接待決 #16 的「依語系去重各產一份」（見 §7.3），不必另開一張表。
> 因此 `ReportDefinition` **不保留** `RecipientUserIds` 屬性，改為 `ICollection<ReportRecipient>` 導覽屬性。

**(c) `ParamsJson` 的反序列化必須走白名單，且要能承受工具參數演進。**
依 `ToolKey` 決定目標型別時，**不得用 `Type.GetType()` 或多型反序列化**（這是把使用者資料變成型別載入的典型破口），
一律由 `ReportToolCatalog` 提供 `ToolKey → Type` 的靜態映射。
另外：工具日後新增/移除參數時，舊 `ReportDefinition` 的 `ParamsJson` 會反序列化失敗——
**不得靜默跳過**（同 §7.8 S2 精神），須標示為「定義已失效，請重新儲存」並寫 `ReportRun.Status = Failed`。

### 7.1 執行基礎設施（✅ **2026-08-04 定案：方案 A，`BackgroundService`**）

> ⚠️ **事實更正（2026-08-03 查證）**：初版本節寫成走 Hangfire `RecurringJob.AddOrUpdate`，
> 但**本專案並未安裝 Hangfire**（`SmartFactory.MES.csproj` 無此 PackageReference）。
> 現行所有排程一律是 `IHostedService` / `BackgroundService`，共 7 支 `AddHostedService` 註冊，例如：
> [`DailyDataAuditJobService`](../../../Modules/ACT/Services/Jobs/DailyDataAuditJobService.cs)、
> [`AuditLogRetentionJobService`](../../../Modules/ACT/Services/Jobs/AuditLogRetentionJobService.cs)、
> [`EtaRecalibrationBackgroundService`](../../../Modules/MFG/Services/EtaRecalibrationBackgroundService.cs)。
>
> **Phase 0 是整份規劃的地基，不能架在不存在的套件上。此項須先定案：**

| 方案 | 做法 | 代價 |
|---|---|---|
| **A. 沿用既有 BackgroundService 模式（傾向此案）** | 單一 `ReportDispatcherBackgroundService`，定時掃 `ReportDefinition` 比對 `NextRunAt` 到期者執行 | 零新依賴、與既有 7 支 job 風格一致；需自行處理排程時刻計算、錯過補跑（§7.6）、單機假設（§7.7） |
| **B. 引進 Hangfire** | 依原規劃 `RecurringJob.AddOrUpdate` | 新增套件與**自己一套資料表 schema**（會與既有 migration 管理、乾淨部署流程互動）、需評估授權條款；換得儀表板、重試、分散式鎖 |

專案的 [`/new-job`](../../commands/new-job.md) 規格 Step 0 已定調：
**預設選 `BackgroundService`**，只有「跨重啟仍要重試」或「需要管理介面」或「多副本叢集」才改用 Hangfire。
報表排程三者皆不成立（見 §7.6、§7.7）→ **方案 A 符合既有慣例**。

> ✅ **定案：方案 A**（待決 #1 結案）。零新依賴、零新 schema，與既有 7 支 `AddHostedService` 同風格。
> 代價（排程時刻計算自理）由 §7.2 的受限列舉吃掉——不是 Cron，就只有「每日／每週幾／每月幾號」三種算式。
> 日後真要 Hangfire，`ReportDefinition` 這張表不必改，只換派工器。

方案 A 的骨架須比照 [`DailyDataAuditJobService`](../../../Modules/ACT/Services/Jobs/DailyDataAuditJobService.cs) 的既有範式：

```csharp
/// <summary>報表排程派工（每分鐘掃描 NextRunAt 到期的 ReportDefinition）。</summary>
public sealed class ReportDispatcherBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;   // 本體是 Singleton，DbContext 是 Scoped
    private readonly ILogger<ReportDispatcherBackgroundService> _logger;

    internal const int PollSeconds = 60;                    // internal = 測試可讀

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(PollSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }   // 關機路徑與失敗路徑必須分開接

            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { /* 關機，忽略 */ }
            catch (Exception ex) { _logger.LogError(ex, "報表排程派工失敗"); }
        }
    }

    /// <summary>單次派工；internal 是刻意的測試接縫（比照既有 3 支 ACT job）。</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        // 每筆定義各自 scope + 各自設定 DbContext 上下文三屬性（§5.5 T2）
    }
}
```

四個必須照抄的既有慣例：`sealed`、`IServiceScopeFactory`（每次執行開新 scope）、
`internal` 的 `RunOnceAsync`（測試接縫）、`OperationCanceledException` 與一般例外分開 catch。

### 7.2 排程表達力：不要一開始就做完整 Cron

實測：專案**沒有安裝任何 Cron 解析套件**（無 `Cronos`、無 `NCrontab`），
而既有 3 支 ACT 排程根本沒用 Cron，都是手寫的「每日固定小時」：

```csharp
var now  = DateTimeOffset.Now;
var next = now.Date.AddDays(now.Hour >= RunAtHour ? 1 : 0).AddHours(RunAtHour);
```

例行報表的真實需求（每日／每週一／每月 1 號）用一個列舉就能表達，**不需要 Cron 的表達力**：

```csharp
// 型別名刻意不叫 ScheduleKind：屬性同名會讓類別內的 ScheduleKind.Daily 解析到屬性而非型別
public enum ReportScheduleKind { Daily, Weekly, Monthly }
```

→ 建議第一版用 `ScheduleKind + 時分（+ 週幾／幾號）`，**零新依賴**，
且使用者介面是三個下拉選單而不是要人填 `0 2 1 * *`。
真的出現「每季最後一個工作日」這類需求時再評估引進 `Cronos`（屆時 `ScheduleKind` 加一個 `Cron` 分支即可，不必重做）。

> ✅ **2026-08-04 定案：受限列舉**（待決 #13 結案）。
> 兩個必須寫進實作的邊界條件：
> ① **Monthly 的 31 號**——2 月沒有 31 號，須明訂「取當月最後一日」而非跳過該月（跳過＝該月靜默沒有報表）。
> ② 排程時刻一律以 `NextRunAt`（UTC）落庫，計算時用 `IDateTimeService` 的時區換算，
> 不可直接用 `DateTime.Now`——否則跨時區部署會在錯誤的當地時間送出。

### 7.3 輸出通道（**事實更正：專案沒有 Email 基礎設施**）

> ⚠️ **2026-08-03 查證**：初版 `OutputFormat` 寫 `Excel / Email / 畫面`，但**本專案完全沒有寄信能力**——
> 全庫查無 `SmtpClient`、`IEmailSender`、`MailKit`、`MailMessage`，`appsettings` 亦無任何 SMTP 設定。
> 「Email」這個輸出通道**今天不可實作**。

現有可用的推送能力：

| 能力 | 現況 |
|---|---|
| Excel / CSV / JSON 產生 | ✅ [`IExportService`](../../../Services/ExportService.cs)（`ToExcel` / `ToCsv` / `ToJson`，ClosedXML） |
| Web Push 推播 | ✅ `PushNotificationService` ＋ `PushSubscriptions` 表（`WebPush 1.0.11`） |
| Email | ❌ 無 |

三個選項：

| 選項 | 做法 | 評價 |
|---|---|---|
| **E1 站內收件匣（傾向此案）** | 報表產生後落地成檔案 ＋ 寫 `ReportRun` 紀錄 ＋ Web Push 通知「您的報表已產生」＋ 畫面下載 | **零新依賴**；且下載時可以**再檢查一次權限**（§7.8 S5），這是 Email 附件做不到的 |
| E2 引進 SMTP（MailKit 等） | 新套件 ＋ SMTP 設定 ＋ 寄件失敗重試 | 機密報表附件**一旦寄出就永久脫離 RBAC**——調職、離職後附件仍在信箱裡，與 §八 的整套防線直接衝突 |
| E3 只產生不推送 | 使用者自己到畫面看 | 最省，但排程的價值大打折扣 |

→ 建議 E1，並把「**機密等級高的報表禁止走 Email 附件通道**」寫成架構約束（即使日後引進 SMTP）。

> ✅ **2026-08-04 定案：E1 站內收件匣**（待決 #12 結案），並把上述禁令升為**架構約束**：
> 日後即使引進 SMTP，機密等級（§8.1 裁決結果）為「機密」者一律不得走附件通道。
> 理由不是成本，是 **E2 會單方面作廢 §八 整章**——附件寄出即永久脫離 RBAC，
> 而 §7.8 S5「下載時再檢查一次權限」只有 E1 做得到。
> 落地要點：E1 的 Web Push 通知內文**只寫「您的報表已產生」，不得帶任何數字或報表名稱以外的內容**——
> 推播會顯示在鎖定畫面，等於繞過登入態展示機密（同 §8.7 洩漏路徑的精神）。

> **輸出語系（§5.5 T7 的落地細節）**：[`ExportRequest.Headers`](../../../Services/ExportService.cs) 是呼叫端傳入的 `string[]`，
> `IExportService` 本身不做在地化。畫面路徑的欄位標題來自 `IViewLocalizer`／`IStringLocalizer`，
> 而語系是由請求管線設定的 `CultureInfo.CurrentUICulture` 決定——**排程沒有請求管線**，
> 拿到的是行程預設語系。英文使用者會收到一份中文標題的 Excel（數字正確，但沒人會回報這算 bug）。
> **這不是假設性風險**：系統 UI、驗證訊息與使用手冊（§10.1）都已是中英雙語，英文使用者是真實存在的族群。
> → 排程執行時必須**顯式**依 `ApplicationUser.Language` 設定 `CultureInfo.CurrentUICulture`（或改注入 `IStringLocalizer` 並顯式帶 culture）。
>
> ✅ **2026-08-04 定案（待決 #16 結案）：依「收件人語系去重」各產一份，不是每人一份。**
> 作法：取 `ReportRecipient.Language` 的相異值分組（`null` 者退回 owner 語系），
> **每個語系執行一次匯出、產生一個檔**，`ReportRun` 對每個語系各記一列（`RunAt` 相同、`FilePath` 不同）。
> 十個中文收件人仍然只產一個檔；中英混合的收件清單產兩個檔。
> **不可用「owner 語系一份」草率結案**——本系統已是雙語部署，那等於讓英文使用者固定收到中文報表，
> 而且數字是對的，所以永遠不會有人回報。

#### 為什麼不是「每人一份」

每人一份會讓 `ReportRun` 與檔案數隨收件人線性膨脹（20 人的月報＝20 個內容完全相同的檔），
保留期清理與 §7.8 S5 的下載權限檢查都要多做 20 次。語系是**唯一**會改變檔案內容的收件人屬性，
按它去重就足夠；其餘差異（誰能下載）本來就在下載當下才檢查。

### 7.4 執行紀錄 `ReportRun`（必要，非選配）

比照既有 [`DataAuditRun`](../../../Modules/ACT/Models/DataAuditRun.cs) 的範式（`RunId` / `RunAt` / `TriggerType` / `PlantId` / `DurationMs` / `CreatedBy`），報表排程須有對應的執行紀錄表：

```csharp
public class ReportRun
{
    public long RunId { get; set; }
    public int  DefinitionId { get; set; }
    public DateTime RunAt { get; set; }                  // UTC
    public string TriggerType { get; set; } = "";        // Scheduled / Manual / Nl（Phase 2）
    public string Status { get; set; } = "";             // Success / Failed / SkippedNoPermission
    public DateOnly ResolvedFrom { get; set; }           // RelativePeriod 解算後的實際區間
    public DateOnly ResolvedTo { get; set; }
    public int  RowCount { get; set; }
    public long DurationMs { get; set; }
    public string? FilePath { get; set; }
    public string? ErrorMessage { get; set; }
    public string CreatedBy { get; set; } = "SYSTEM";
}
```

三個都是硬需求，不是為了好看：

1. **§8.6(c) 全量稽核**要求機密查詢事後可追——排程執行也是查詢。
2. **§7.8 S2**（owner 權限失效要停排程並通知）需要一個地方記錄「為什麼停了」。
3. **§5.2.1 規則 4**（解算後的絕對區間必須回顯）——使用者收到報表要能確認「上個月」被解成哪一段。

### 7.5 檔案落地與保留期

`IExportService` 回傳的是 `byte[]`，畫面路徑直接 `File()` 回傳給瀏覽器；但排程沒有 HTTP 回應可寫，**必須落地**。

> ⚠️ **不可放 `wwwroot`**。專案唯一既有的檔案落地點是 `wwwroot/uploads`（`SystemParamController` 存公司 logo），
> 但 `wwwroot` 是靜態公開路徑，**任何人猜到檔名就能直接下載，完全繞過 RBAC**。
> 機密報表落在那裡等於把 §八 整章作廢。

正確做法：存到 ContentRoot 外的設定目錄（如 `Reports:StoragePath`），檔名用不可猜測的 GUID，
**一律經由 Controller 檢查權限後才 `File()` 回傳**。

保留期清理比照 [`AuditLogRetentionJobService`](../../../Modules/ACT/Services/Jobs/AuditLogRetentionJobService.cs) 的既有範式
（`IConfiguration` 讀天數 ＋ `ResolveRetentionDays()` 純函式 ＋ 每日固定時刻，且**時刻要與既有 02:00／03:00 錯開**）。

#### ⚠️ 這個專案今天**沒有任何檔案備份機制**（2026-08-04 查證，待決 #20）

[`scripts/backup-db.sh`](../../../scripts/backup-db.sh) 只做 `pg_dump`——連既有唯一的落地點
`wwwroot/uploads`（公司 logo）都不在備份範圍。報表落地之後會冒出三個沒人負責的問題：

| 問題 | 說明 |
|---|---|
| 備份與異地 | 備份腳本已支援 gpg 加密＋rclone 異地，**檔案這條線完全沒有對應** |
| 保留期與備份互相打架 | `RetentionDays` 把機密報表清掉了，備份裡還留著——機密資料的保留期形同失效 |
| **還原後 DB 與檔案不同步** | DB 還原到昨天、檔案是今天的 → `ReportRun.FilePath` 指向不存在的檔，使用者只看到下載失敗 |

→ **建議：把 `backup-db.sh` 擴充為「DB ＋ 報表目錄同一次打包」**，讓還原天然對齊（兩者取自同一時點），
並讓備份的保留期與 `Reports:RetentionDays` 一致。
替代方案是把報表檔存進 DB（`bytea`），一次解決三件事，代價是備份體積與 `pg_dump` 時間隨報表量成長。
**這件事必須在 P2 檔位（`ReportTools`）上線前決定，不是 P3 的問題**（見 §17.9）。

### 7.6 錯過補跑（missed run）

停機期間錯過的排程要不要補？**建議不補跑，只記錄 `Status = Skipped`。**

理由：報表是快照。`LastMonth` 在停機三天後補跑結果完全相同（無害），
但 `Yesterday` 補跑就會拿到錯的一天——**同一個補跑策略對不同 `RelativePeriod` 語意不一致**，
與其做一套複雜的分類邏輯，不如不補跑並讓使用者手動重跑（常用查詢一鍵重跑，正好是 §7.9 的能力）。

### 7.7 多副本與去重

[`/new-job`](../../commands/new-job.md) Step 0 已載明：跨多副本叢集用 Hangfire 或 K8s CronJob，**不要兩邊都跑**。
現行為單機部署假設（§11.2 C6 亦同）。

若日後 scale-out，`ReportRun` 表可直接當**零依賴的分散式鎖**：
對 `(DefinitionId, 預定執行時刻)` 建唯一索引，多副本同時搶插入時只有一個成功，其餘吃 23505 直接跳過。
這比為了排程而引進 Redis 便宜得多。

### 7.8 收件人與權限時效（**原版缺漏**）

原版僅寫「收件人設定需過權限檢查」，但漏了更常見的洩漏路徑：**owner 的權限會隨時間失效**。

| 規則 | 說明 |
|---|---|
| S1 | **每次執行時重新解析 owner 的權限與資料範圍**，不可只在建立排程時檢查一次。建立時是業務主管、半年後調職，排程仍在每月寄出銷售明細——這是真實的洩漏路徑 |
| S2 | owner 權限已失效（角色移除、帳號停用、失去該工具的 MenuKey 權限）→ **`IsActive = false` 停止排程並通知管理者**，寫入 `ReportRun.Status = SkippedNoPermission`；不得靜默續寄，也不得靜默停止 |
| S3 | 收件人限系統內部使用者（以 `UserId` 設定，非任意 email 字串），且每個收件人都必須通過該工具的權限檢查 |
| S4 | 執行時的授權範圍由 `OwnerRoleId` 顯式解析後設定進 `ApplicationDbContext` 的三個上下文屬性（§5.5 T2） |
| S5 | **下載時再檢查一次**。已產生的報表檔不因「產生當下有權限」而永久可下載——這是 E1 站內收件匣優於 Email 附件的關鍵理由（§7.3） |

### 7.9 常用查詢（「我的查詢」）

**可行，而且成本近乎為零**——它就是 `ScheduleKind = null` 的 `ReportDefinition`，不需要另一張表、另一套機制。

實測：專案目前**沒有任何**儲存查詢／我的最愛機制（全庫查無 `Favorite` / `SavedQuery` / `SavedFilter`），
所以這是淨新增功能，沒有既有慣例要對齊。

#### 三個層次（可獨立取捨）

| 層次 | 做法 | 成本 | 何時可用 |
|---|---|---|---|
| **L1 記住上次查詢條件** | 前端 `sessionStorage` 存查詢字串，回到該報表時自動帶回 | 極低 | **現在就能做，不必等 Phase 0** |
| **L2 具名常用查詢（建議）** | `ReportDefinition` ＋ `ScheduleKind = null` | 隨 Phase 0-B 附帶 | Phase 0-B |
| **L3 分享給他人／角色** | L2 ＋ `Visibility`（Private / Role / Plant）欄位 | 中 | **延後**（Phase 0-B 之後，見下） |

> ✅ **2026-08-04 定案（待決 #14）：L3 延後，但現在就把約束釘死。**
> L1／L2 先做；L3 日後要做時**必須**滿足：執行分享來的常用查詢時，
> 一律以**檢視者**身分解析權限與資料範圍，絕不沿用建立者的 `OwnerRoleId`。
> 這一條寫在這裡是因為它會被 §7.8 S4「執行時由 `OwnerRoleId` 解析範圍」**反向誤導**——
> 那條是給排程用的（排程沒有檢視者，只有 owner），照抄到分享情境就成了權限提升管道：
> 業務主管把「全公司銷售排行」分享給作業員，作業員點開就以主管範圍執行。
> → 實作時 `ReportDefinition` 的執行入口必須明確區分「排程觸發（用 owner）」與「互動觸發（用當下使用者）」兩條路徑。

> **L1 的注意事項**：查詢條件本身可能含機密（特定客戶代號、特定料號）。
> 共用電腦上 `localStorage` 會殘留給下一個登入者 → **用 `sessionStorage` 而非 `localStorage`**，並在登出時清除。

#### L2 的設計要點

1. **期間必須存 `RelativePeriod`，不可存絕對日期。**
   這一點原本寫在 §5.2.1 且標為「Phase 3 的必要條件」——**實際上常用查詢（Phase 0-B）就需要它了**。
   若「上月銷售排行」存的是 `2026-07-01~2026-07-31`，下個月點開還是七月的資料，
   那正好是「常用查詢」的反面。→ **`RelativePeriod` 的必要性從 Phase 3 提前到 Phase 0-B。**
2. **存參數，不存結果。** 每次點開都重新查、重新走 §8.4 的 L2 權限檢查。
   **絕不可快取查詢結果**——否則使用者調職後點開舊的常用查詢，還看得到不該看的數字。
3. **權限失效要顯示而非消失。** 使用者失去目標報表權限時，常用查詢在清單中標示為「無權限」並禁用，
   不要靜默隱藏（同 S2 精神：靜默停止與靜默續行一樣糟）。
4. **常用查詢本身不需要新的 MenuKey 權限。** 它只是參數容器；執行時檢查的是**目標報表的 MenuKey**。
   這避免了為一個附屬功能新增權限維度（呼應 §8.6(a) 的思路）。
5. **唯一鍵 `(OwnerUserId, Name)`**，避免同一人存出三個都叫「上月銷售」的查詢。
6. **入口放報表畫面而非集中維護畫面。** 報表查詢完加一顆「儲存此查詢」，側欄列「我的常用查詢」；
   兩者與排程維護畫面共用同一個 controller 與同一張表。集中式維護畫面留給管理者看全部排程。

#### ✅ 落地紀錄（2026-08-05）

L2 已交付。與規劃相比有三處實作決定值得記下：

1. **`ReportDefinition.PlantId` 是 `string(20)` 不是 `int?`。** §7.0 的程式碼片段寫成 `int?`，
   但本專案的 `Plant.Id` 是字串代碼（P1／P2），照抄會對不上。
2. **畫面繫結不放進 `IReportToolDescriptor`。** 「這支工具對應哪個畫面、吃哪些查詢列欄位」
   是 MVC 這一側的事，排程與 LLM 都用不到；放進工具契約會讓契約背上 UI 細節。
   → 另立 `ReportScreenBindings`（工具鍵 → Controller／是否吃日期區間／**參數白名單**）。
   白名單是安全機制不是文件：`ParamsJson` 會被展開成目標畫面的查詢字串，
   不過濾等於開放對該畫面注入任意參數；存檔與執行兩端各過一次。
3. **期間存列舉名稱而非序數。** `RelativePeriod` 現有 17 個成員，日後在中間插一個新成員，
   以序數存下來的舊查詢會默默指向另一個期間——畫面照常顯示，只是數字全錯。

守門測試：單元 22 支（`SavedQueryServiceTests`）＋真 PG／真 HTTP 整合 9 支
（`SavedQueryIntegrationTests`）。整合測試不是為了覆蓋率——共用 partial 的檢視路徑解析
與轉導網址組字串這兩處，**建置與單元測試都看不到，只有真的打開畫面才會炸**
（實測：拿掉 `RPSModule` 的 Shared 檢視路徑，六張報表全部 500，而建置照樣過）。

#### 對後續階段的加成

- **Phase 3 的閉環提前實現**：使用者可以「先存常用查詢 → 用順了 → 加排程」，全程不需要 LLM。
  → **2026-08-24 追加 NL 入口**（Phase 3）：S-7 的資料查詢結果下方多一個「儲存此查詢」，
  走的是**同一個** `ISavedQueryService.SaveAsync`。刻意共用而不另寫一條「NL 專用的存檔」——
  權限、白名單過濾、同名覆寫、期間必填的規則因此只有一份，否則差異會落在
  「用問的存下來的」與「用點的存下來的」之間，而那是最不該有差異的地方。
  兩套參數詞彙的對照見 §十三 Phase 3 交付紀錄。
- **Phase 2 的實證輸入**：使用者**實際存了什麼查詢**，比 §9.2-1 的「問了什麼」更能反映真實需求，
  是決定 NL 工具目錄優先順序的第一手資料。
- **降低 NL 的必要性**：很多「想用自然語言問」的需求，本質是「不想每次重填查詢條件」。
  常用查詢直接解決這一類，而且沒有任何幻覺風險。

## 八、資料權限與機密資料邊界（**新增章節**）

銷售額、毛利、成本等屬機密資料，**不是任何登入者都能問**。本章是 §九「安全邊界」的資料面補充。

核心判斷：**風險不在「有沒有檢查權限」，而在「NL 入口是否比畫面寬鬆」**。既有畫面已有權限檢查，NL 入口若採用不同的檢查慣例，會以「權限重構」的樣子悄悄放寬。以下三個破口在導入前必須處理。

### 8.1 破口一：報表畫面的權限動詞不一致（**須先於工具化處理**）

查證現行報表 Controller 的 `Index` 閘門，動詞並不一致。純報表型 Controller（無 `Create`/`Edit`/`Delete` action）實測 **61 支：`CanCreate` 33 支、`CanRead` 28 支**：

> 📋 **Phase 0-A0 已完成（2026-08-03）**：完整盤點表見 [`report-permission-inventory.md`](../report-permission-inventory.md)——
> 逐張列出模組、現行閘門、是否含金額、**今天實際可見的角色**，並分成四組（⓪ 無閘門／① 維持現狀／② 已過寬／③ 收緊型）。
> 該檔改用更嚴格的母體定義（**完全無 `[HttpPost]`** ＋ 3 支報表型例外 ＝ **52 支**），
> 把 `MonthEnd`／`NrvAssessment`／`Schedule`／`BatchRelease` 等**執行型**畫面排除——它們的 `CanCreate` 閘門本來就是對的。
> **需要業務裁決的只剩 28 張**（② 13 ＋ ③ 15），其餘 22 張維持現狀不必問。
>
> ⚠️ 盤點另外抓到本文件原先未預期的一整類，且規模遠超預期：**全庫 61 支選單畫面的 `Index` 根本沒有讀取閘門**
> （只呼叫 `PrepareIndexPermissionsAsync` 或只做 `ViewBag.CanX` 賦值——兩者都不 `Forbid()`；專案也無全域權限 filter），
> 含 `SalesOrder`／`WorkOrder`／`ProductMaster`／`Ncr` 等核心作業畫面。
> 「照抄畫面現行檢查」對這些等於照抄「沒有檢查」。
> **已於 2026-08-03 全數修復（60 支補閘門，`TwoFactorAdmin` 另有 admin 檢查且屬 Core 檔）
> 並新增一致性規則 R23 守門、baseline 清零**；單元 2638、整合 237 全綠。

| 以 `CanCreate(MenuKey)` 當查詢閘門 | 以 `CanRead(MenuKey)` 當查詢閘門 |
|---|---|
| SalesRanking、OrderOverdue、DeliveryRate、ReturnRate、PurchaseOverdue、APAging、ARAging、InventoryValuation、StockShortage、SlowMovingInventory、DefectRate、WorkOrderProgress、WorkOrderVariance、GrossMargin、ProfitLoss、BalanceSheet、Dashboard、OutsourcePerformance、MonthEnd、Nrv、Ecl… | SalesTrend、SalesForecast、**MarginAnalysis**、**ContributionMargin**、PurchaseAmount、PurchasePrice、SupplierDeliveryRate、SpcAnalysis、InventoryAging、InventoryTurnover、InventoryValue、StockVariance、BadDebtProvision、Oee、Utilization、ProductionEfficiency、WipReport、MrpAchievement、MachineDowntime、DataAudit… |

#### 機制：`CanRead` 恆為 `CanCreate` 的真超集（2026-08-03 實測）

[`MenuService.cs:12`](../../../Services/MenuService.cs#L12) 的 `MenuItem.SupportedActions` 預設值即為五項全開：

```csharp
public List<string> SupportedActions { get; set; } = new List<string> { "Read", "Create", "Update", "Delete", "Print" };
```

而 [`ProjectZMenuContributor`](../../../Services/ProjectZMenuContributor.cs) 的報表項目**全部未覆寫此預設**。搭配 [`RolePermissionSeeder.cs:249-250`](../../../Services/RolePermissionSeeder.cs#L249-L250)：

```csharp
CanRead   = can("Read");                              // 不看 access 級別：Access.Read 與 Access.Full 皆給
CanCreate = access == Access.Full && can("Create");   // 僅 Access.Full 才給
```

→ 在任一報表 MenuKey 上，**`CanRead` 恆為 `CanCreate` 的真超集**。閘門動詞改一個字，可見範圍立即擴大。

更關鍵的是：`MenuPermission` 資料列中的 `CanRead` **早就已經是 `true`**（種權限時就寫入了）。
因此這個變更**不需要任何資料異動、不產生 migration、不觸發任何稽核事件、事後也無跡可循**。
這就是本節開頭所謂「以權限重構的樣子悄悄放寬」的具體機制——它是一次沒有痕跡的權限擴散。

#### 若統一改為 `CanRead` 的實際擴散範圍

依 [`RolePermissionSeeder.cs:311-330`](../../../Services/RolePermissionSeeder.cs#L311-L330) 的模組×角色矩陣（欄序：HQ, SALES, WH, PROC, FIN, MGR, OP；`F`=Full、`R`=Read、`N`=None）：

| 報表 | 模組矩陣 | 今天可見角色 | 改用 `CanRead` 後 |
|---|---|---|---|
| GrossMargin 毛利／ProfitLoss 損益／BalanceSheet 資產負債／Dashboard／OutsourcePerformance | RPT `(F,R,R,R,R,R,R)` | HQ ＋ ADMIN | **全 7 角色，含 OP 作業員** |
| SalesRanking 銷售排行 | ORD `(F,F,R,R,R,R,N)` | HQ、SALES | ＋WH、PROC、FIN、MGR |
| InventoryValuation 存貨價值 | INV `(F,R,F,R,R,R,R)` | HQ、WH | **全 7 角色** |
| APAging／ARAging／MonthEnd／Nrv／Ecl | FIN·ACT `(F,N,N,N,F,R,N)` | HQ、FIN | ＋MGR |

#### 反向發現：既有暴露（與 LLM 無關，今天就在發生）

同屬 RPT 選單群組、機密等級相同的三張毛利類報表，閘門卻不同：

| 畫面 | 閘門 | 實際可見角色 |
|---|---|---|
| [`GrossMarginController`](../../../Modules/RPT/Controllers/GrossMarginController.cs) 毛利分析 | `CanCreate` | 僅 HQ（＋ADMIN） |
| [`MarginAnalysisController`](../../../Modules/RPT/Controllers/MarginAnalysisController.cs) 多維度毛利 | `CanRead` | **全 7 角色** |
| [`ContributionMarginController:59`](../../../Modules/RPT/Controllers/ContributionMarginController.cs#L59) 邊際貢獻 | `CanRead` | **全 7 角色（含 OP 作業員）** |

這不是設計出來的分級，是不同時期開發慣例的副產物。**邊際貢獻與多維度毛利今天連作業員都能開啟。**

→ 此項應**獨立當作既有缺陷立即處理，不要綁進 LLM 專案的排程**。它今天就在洩漏，不該等 Phase 0。

> ✅ **2026-08-04 已解除**：三張毛利類報表現在一致地由 `CanRead` 把關，可見角色統一為 HQ／FIN／MGR
> （見 inventory §四之二）。作業員與業務、倉庫、採購都不再看得到毛利與邊際貢獻。
> 更重要的是**分級不再靠動詞**——本節整段描述的「改一個字就無痕放寬」的機制仍然成立，
> 但機密範圍已改由 `MenuPermission` 資料承載，動詞層面沒有可以誤改的東西了。

#### 結論

- ❌ **不可**把 `IReportTool` 的權限 Attribute 統一宣告為 `CanRead`。那會讓今天在畫面上被 `Forbid()` 擋掉的人，明天用自然語言就查得到銷售額。
- ✅ 工具的權限宣告**必須逐一對齊該報表畫面現行的實際檢查**。
- ✅ 順序上，應先獨立處理「報表畫面權限動詞混亂」這個既有問題（與 LLM 無關，本來就該做），確定每張報表的機密等級後，工具層再照抄結果。**這 61 張報表的機密等級是業務決策，不是技術決策**，須由業務端逐張裁決（見 §15.2）。
- ⚠️ **`CanPrint` 的落差是新的洩漏面**。`CanPrint = access == Access.Full && can("Print")`，代表唯讀角色**看得到畫面但匯不出 Excel**——這是現行系統刻意的資料出境管制。而 NL 回答本質上就是一條繞過 `CanPrint` 的出境管道。工具層必須先定義「NL 回答算 `Read` 還是 `Print`」，否則等於單方面取消既有管制（列入待決 #9）。
>
> **2026-08-12 使用者裁決（經 dev-f1 轉述）：NL 回答算 `Read`。**
> **上面那段風險敘述不刪，因為它沒有消失——只是被接受了。** 裁決之後的事實是：
> P3 開燈後，配方／產能類（BOM、Routing、OEE）**確實會經由 NL 出境**，
> 而這一類原本刻意不收 `CanRead`、靠 `CanPrint` 管出境。
> 這是**知情的取捨**，不是遺漏：留著這段是為了讓日後任何人問「當初知不知道」時，
> 答案是白紙黑字的「知道」。若哪天要收回，收回的是裁決，不是重新發現風險。

#### 8.1-終 剩餘 7 支 ⛔ **2026-08-05 裁決已於 2026-08-14 推翻**

> **現行結論：這 7 支已改為 `CanRead`，全庫 30 支報表閘門一律 `CanRead`。**
> 完整理由、放寬範圍逐支對照、以及為什麼不做 §四之二 那種一次性校正，見
> [`report-permission-inventory.md` §四之三](../report-permission-inventory.md)。
> 以下保留 2026-08-05 的原文，因為被推翻的是**推論**不是**事實**——
> 「改 `CanRead` 是放寬」當時就量清楚了，這次是在知情的前提下決定放寬。

<details>
<summary>2026-08-05 原裁決（已失效，保留備查）</summary>


Phase 0-A 收斂後全庫複查：30 支報表型 Controller 中 23 支為 `CanRead`，**剩 7 支仍是 `CanCreate`**：

| 模組 | Controller |
|---|---|
| INV | `StockShortageController` |
| MFG | `DefectRateController`、`WorkOrderProgressController` |
| ORD | `DeliveryRateController`、`OrderOverdueController`、`ReturnRateController` |
| PUR | `PurchaseOverdueController` |

共同點是**都不含金額**，所以不在 §14 決議 #2 那 28 支機密清單內，因而沒被一起改到。

**裁決：維持 `CanCreate`，不改。** 理由：改成 `CanRead` 的方向是**放寬**而非收緊
（`CanRead` 恆為 `CanCreate` 真超集，見前一節的機制證明），會讓「只有讀取權」的角色
從看不到這 7 張報表變成看得到。這 7 張是缺料／不良／逾期／準交等現場營運資訊，
目前限縮在 `Access.Full` 角色是既有且刻意的範圍，沒有業務需求要擴大。

**因此 §6.1 前置作業一到此收斂完畢**，Phase 0-B 的工具層權限宣告照抄現況即可：
23 支宣告 `CanRead`、這 7 支宣告 `CanCreate`。

> 現況已由特徵測試鎖住（`OrderOverdue`／`ReturnRate`／`StockShortage` 三支各一條）。
> 日後若有人在重構中把動詞改成 `CanRead`，測試會紅——這正是要的：
> 那是一次存取範圍擴大，必須推翻本裁決，不可無聲發生。

</details>

> 📌 上面最後那段的機制**照原樣留著**，只是方向反過來：那三支特徵測試現在鎖 `CanRead`，
> 而且**兩個方向都斷言**（只有 `CanCreate` 要被擋、只有 `CanRead` 要進得去）。
> 推翻裁決仍然要經過一次紅燈，不會無聲發生。

### 8.2 破口二：`IgnoreQueryFilters` 同時關掉兩層過濾

非測試碼中 `IgnoreQueryFilters` 出現 **307 處**（`Modules` / `Services` / `Controllers`）。其中一批正是 §六 點名要工具化的報表服務，且是**刻意**繞過過濾器做跨廠彙總：

- [`OtdFactService.cs:35`](../../../Modules/PUR/Services/OtdFactService.cs#L35) — 註解明寫「null＝全公司彙總（供應商評分卡，跨廠 IgnoreQueryFilters）」
- [`VendorEvaluationScoreService.cs:47`](../../../Modules/PUR/Services/VendorEvaluationScoreService.cs#L47) — 同樣為全公司評核

關鍵在於 `ApplicationDbContext.cs:165-210` 的 global filter 是**廠別過濾（`IPlantEntity`）與組織層級資料權限過濾（`IDataAuthEntity`）兩者 AND 組合**，`IgnoreQueryFilters` 會**一次關掉兩層**，不是只關廠別。

這些服務原本被高權限的評分卡畫面包著呼叫，是安全的；**一旦裸露成 `IReportTool` 供 LLM 呼叫，就是「只該看自己部門的人問到全公司資料」**。處理方式見 §5.5 T1（改為接受已授權範圍參數），並以一致性規則守門。

### 8.3 破口三：資料權限過濾的 legacy 放行條款

`ApplicationDbContext.cs:186-203` 的組合條件為：

```
string.IsNullOrEmpty(CurrentRolePath)
    ? e.CreatedByRoleId == CurrentRoleId
    : (string.IsNullOrEmpty(e.OrganizationUnitPath) || e.OrganizationUnitPath.StartsWith(CurrentRolePath))
```

註解說明 `OrganizationUnitPath` 為空的資料是「DataAuth 啟用前的歷史資料，視為公開」。

- 在**單據畫面**上，這代表偶爾看到一筆歷史資料，影響有限。
- 在**彙總報表**上，這代表所有這類歷史資料會被無聲加總進金額——**機密金額從後門流出，且數字自洽、沒人會發現**。

→ 工具化前必須先確認歷史資料的 `OrganizationUnitPath` 回填狀況；未回填前，機密類工具應顯式排除 `OrganizationUnitPath` 為空的資料或明確標示口徑。

> **2026-08-13 交付作業程序（待決 #3／#11，走 §15.6 路徑 A）**：
> [`deploy/orgpath-backfill.md`](../../../deploy/orgpath-backfill.md) ＋
> [`scripts/orgpath-inventory.sql`](../../../scripts/orgpath-inventory.sql)（唯讀盤點）
> ＋ [`scripts/orgpath-backfill.sql`](../../../scripts/orgpath-backfill.sql)（預設試跑、`-v apply=1` 才套用、冪等）。
> 兩支都以 `information_schema` 動態尋表（同時有 `OrganizationUnitPath` 與 `CreatedByRoleId` 兩欄者），
> 新增實體不需回頭改腳本。**開發區不執行**，正式區於 P2 開燈前與待決 #20 同一時機執行。
>
> 實測帶出一個原本沒寫在本節的限制：回填只補得到「建立者角色有掛組織單位」的列，
> 而 `RolePermissionSeeder` 刻意只讓 view-all 角色（`1`／`HQMGR`／`MGR`）掛根 OU——
> 其餘角色 `CurrentRolePath` 為空、走的是 `CreatedByRoleId == CurrentRoleId` 那條分支。
> 開發區實測 119 列中只有 34 列補得動。**所以「回填完就沒事了」是錯的，殘留必然存在**，
> 涵蓋率要先跑盤點才知道。

### 8.4 分層防線（NL 入口）

| 層 | 機制 | 說明 |
|---|---|---|
| **L0 認證** | 必須為已登入使用者，且工具以**呼叫者身分**在請求範圍內執行 | 登入是必要條件，但幾乎不構成防線 |
| **L1 目錄可見性** | **組 prompt 之前就把使用者無權的工具從候選清單移除** | 見下方說明，投報率最高的一招 |
| **L2 執行檢查** | 工具執行時仍再檢查一次權限與範圍（縱深防禦），不信任 L1 | L1 是效能與資訊隱藏，L2 才是保證 |
| **L3 彙總洩漏** | 最小群組數門檻等，見 §8.5 | RBAC 完全擋不住的一類 |
| **L4 輸出與稽核** | 統一拒絕訊息 + 全量稽核，見 §8.6 | 事後可追是機密查詢的必要條件 |

**L1 目錄可見性過濾**是最高投報率的設計：不是「執行時檢查權限」，而是在組 prompt 前就過濾候選工具。三重好處：

1. LLM 根本不知道該工具存在 → 不會生成 → 不需要拒絕。
2. 使用者也無法從拒絕訊息推知「系統有銷售排行這種功能」。
3. **順帶解決 §十一.1 的 context 膨脹**——一般使用者的可見工具本來就遠少於全部，可能因此不需要額外的路由層。

> ⚠️ **L1 的效能陷阱（2026-08-04 查證）**：[`PermissionService.CanRead`](../../../Services/PermissionService.cs#L76-L83)
> 每次呼叫都打一次 `MenuPermissions.AnyAsync`，**沒有任何快取**。
> 機密分級改由 `MenuPermission` 資料承載（§8.1 裁決）之後，L1 過濾＝對每個候選工具各查一次資料庫——
> 60 個工具就是每則提問 60 次查詢，而且發生在使用者等 LLM 回應的路徑上。
> → Phase 2 前必須改成「一次撈該角色全部 `MenuPermission` 再在記憶體判定」或加 per-request 快取。
> 不影響 Phase 0（畫面路徑一次只問一個 MenuKey）。

### 8.5 彙總洩漏（inference leak）——RBAC 擋不住的一類

即使權限判定全部正確，自然語言仍能把彙總查詢逼成明細：

- 「上個月銷售額最高的客戶是誰、多少錢」→ `TopN=1`，實質等於單一客戶明細。
- 「客戶 A 上個月的銷售額」→ 篩選收斂到一筆，彙總即明細。
- 分兩次問「全部」與「排除客戶 A」，相減得到 A —— **差分攻擊**。

畫面上這件事天然被抑制（需手動組合、留下操作痕跡），NL 入口把成本降到零。防護：

| 規則 | 說明 |
|---|---|
| A1 | 機密類工具設**最小群組數門檻**：結果分組數 < 3 或 `TopN` < 3 時，只回名次不回金額，或要求改用畫面查詢 |
| A2 | 「同一使用者短時間內的相似查詢」納入稽核比對，供事後偵測差分攻擊 |
| A3 | 此 guard 實作在工具層共用基底，非各工具自理 |

### 8.6 欄位級權限的替代方案、拒絕訊息、稽核

**（a）用「工具粒度」取代欄位級權限。** 系統目前只有選單級權限，補欄位級成本極高。更划算的做法是**把敏感欄位拆成獨立工具、掛獨立 MenuKey**：例如「銷售排行（僅數量/筆數）」與「銷售排行（含金額）」是兩個工具兩個 MenuKey，用既有 RBAC 就能表達「業務助理看得到排名、看不到金額」。這比新增權限維度便宜一個數量級，且與既有的「選單權限自動補缺鍵」機制相容。

**（b）拒絕訊息統一化。** 無權限時**絕不可回**「你無權查詢客戶 A 的銷售額」——這已洩漏客戶 A 存在，以及系統具備此功能。統一回 §九.2 的「目前沒有對應的查詢工具」，讓「無權」與「無此工具」在外部完全無法區分。

**（c）全量稽核。** 每次查詢（NL 或排程）都須記錄：使用者、工具、解析後參數、回傳筆數、是否被拒、解算後的絕對期間。機密資料查詢的必要條件是**事後可追**，而不只是事前可擋。此紀錄同時是 §九.2-1 擴充工具目錄的實證依據。

> ⚠️ **事實更正（2026-08-03 查證）**：初版寫「寫入既有 DataAuditService 管線」，**兩處都不對**：
>
> 1. [`DataAuditService`](../../../Modules/ACT/Services/DataAuditService.cs) 是**資料一致性稽核**（14 支帳務/庫存對帳檢查），
>    不是操作稽核管線，語意完全不同。
> 2. 真正的操作稽核是 [`AuditInterceptor`](../../../Data/AuditInterceptor.cs)——它是 **`SaveChangesInterceptor`**，
>    只在 `Added` / `Modified` / `Deleted` 時寫 `AuditLog`。**查詢不改任何實體 → 一列稽核都不會產生。**
>    （另注意 `AuditLog : IPlantEntity`，且 `AuditInterceptor` 依賴 `IHttpContextAccessor`，排程情境 `UserId` 會是 null。）
>
> → 「全量稽核」**必須自建顯式寫入路徑**，不存在可以搭順風車的既有管線。
> 好消息是這件事已經有歸宿：排程與手動執行寫 §7.4 的 `ReportRun`，NL 追問寫 §11.3 的 `QaTurn`，
> 兩張表合起來即滿足本項要求——**但這是必須寫的程式，不是設定**。

### 8.7 其他洩漏路徑

| 路徑 | 規則 |
|---|---|
| **SessionState 快取** | 快取鍵必須含 user id；第二輪合併參數後**必須重跑完整權限檢查**，不可沿用第一輪的授權結論（使用者的當前廠別/角色可能在兩輪間切換）。SessionState 是最容易變成權限繞道的元件 |
| **摘要階段資料出境** | **禁止 fallback 到雲端模型 API**（須寫成架構約束而非預設值）。機密資料會完整出現在 prompt 中。同時確認 Ollama 的請求 log 落點與保留期 |
| **RAG 索引汙染** | **業務資料絕不可進入向量庫**。一旦有人為了「回答更完整」把報表結果灌進 pgvector，權限模型即徹底失效——向量庫沒有 row-level security |

## 九、NL 入口的安全邊界

### 9.1 運作方式

LLM 解析自然語言 → 產出結構化 JSON（工具鍵＋參數）→ 應用程式碼驗證參數合法性/權限範圍 → 呼叫對應 `IReportTool` → 執行既有已驗證的查詢邏輯 → 結果回傳，LLM 只做最後的白話摘要。**這整個流程在性質上等同一次內部 API 呼叫**：固定契約、強型別參數、執行前置權限檢查，LLM 不曾直接接觸資料庫。

### 9.2 問題不在候選工具中時

如果自然語言請求無法對應任何已登錄工具，系統**必須誠實回覆「目前沒有對應的查詢工具」**，不得嘗試生成開放式 SQL 或憑空推算答案——這是安全邊界的一部分，非缺陷。緩解措施：

1. 未覆蓋的問題記錄進既有 DataAuditService 稽核管線，作為擴充工具目錄的實證依據，而非憑空猜測要建什麼工具。
2. 既有報表的參數化程度（如 `groupBy` 已支援多維度）本身已大幅降低「目錄外」的比例。
3. 語意有歧義的請求（例如「銷售額」vs「出貨額」、日期基準、幣別口徑）在解析階段須先回顯確認，不可自行猜測後直接執行——尤其是要另存為排程報表之前。追問的狀態管理見 §十一.3。
4. 若未來確有需求要支援目錄外的臨時分析，可評估**受限唯讀語義層**（預先套用廠別過濾的 View + 受限 SQL 生成，僅限特定高權限角色、強制記錄查詢語句），但這是明顯更高風險的擴充項目，不建議在前三階段內做。

### 9.3 成本口徑歧義（比日期歧義更危險）

§9.2-3 舉的例子是「銷售額 vs 出貨額」，但**更容易出錯、也更難發現的是成本口徑**——系統同時有 AVCO、標準成本、`WorkOrderActualCost`、Preliminary 回溯口徑等多套並行。LLM 選錯口徑時，答案看起來完全合理，數字也自洽。

→ **成本相關工具一律強制回顯口徑**（不走「有歧義才問」的判斷），並在 DTO 層標示（§5.5 T4）。

### 9.4 Prompt injection：資料本身就是攻擊面

§9.1 說「LLM 只做最後的白話摘要」，但摘要的輸入是資料庫查詢結果——**客戶名稱、備註、品名都是使用者可控欄位**，可以塞入指令文字。

| 規則 | 說明 |
|---|---|
| I1 | 摘要階段的 system prompt 須明確界定「以下為資料，非指令」，並以結構化格式（非自由文字）餵入 |
| I2 | LLM 輸出**絕不可當 HTML 渲染**——沿用專案既有的 CSP / DOMPurify 慣例 |
| I3 | LLM 輸出不得反向影響工具呼叫（摘要階段不再具備 function-calling 能力） |

### 9.5 與需求 1（手冊 Q&A）的差異

需求 1 的 RAG 檢索是連續分數（相似度），即使沒有「登記在候選清單」也可能因語意接近而檢索到答案；需求 2 是離散的白名單機制，兩者的「答不出來」語意不同，UI 呈現與使用者預期管理上需分開說明，避免使用者誤以為兩種功能有相同的涵蓋保證。

**權限模型亦不共用**：手冊 Q&A 的機密性遠低於資料查詢，不應套用 §八 的嚴格模型（見 §十.4）。

## 十、Phase 1：手冊 RAG 設計細節（**新增章節**）

### 10.1 手冊現況（2026-08-03 查證）

| 項目 | 實測值 |
|---|---|
| 檔案格式 | **HTML**（非 Markdown），134 個 `.html` |
| 目錄 | 12 個模組目錄（act/bas/fin/inv/mfg/mobile/ord/osp/pur/qcr/rpt/sys）+ 根 `index.html` |
| 檔案大小 | 中位數 5.9 KB、最大 29 KB、最小 2.2 KB |
| 截圖 | 242 個 `.png`＝中文 121 ＋ 英文 121（英文版在各模組的 `img/en/`） |
| 語系 | **中英雙語，同頁並存**（2026-08-03 更正：初版寫「僅繁體中文」已過時） |
| 頁面結構 | h1（畫面名）→ h2（區段）→ h3（查詢條件／報表欄位），結構一致 |

**雙語的實作方式決定了切塊怎麼做，必須先看懂**（[`doc-i18n.js`](../../../wwwroot/docs/user/doc-i18n.js) / [`doc-i18n.css`](../../../wwwroot/docs/user/doc-i18n.css)）：

| 承載處 | 機制 |
|---|---|
| 內文 | 每段文字都是**相鄰的 `<span lang="zh">` ＋ `<span lang="en">` 一對**，兩份都在 HTML 裡；顯示與否純由 `html[data-doc-lang]` ＋ CSS `display:none` 決定 |
| `<title>` | `<html data-title-en="...">`，JS 於切換時覆寫 `document.title` |
| 截圖 | `<img src="img/x.png" data-src-en="img/en/x.png">`，JS 換 `src`；英文截圖缺漏時 `onerror` 退回中文截圖 |
| `alt` | `data-alt-en` |
| 語系來源 | `?lang=` → `localStorage` → 預設 `zh`；[`HelpDocResolver`](../../../Services/HelpDocResolver.cs) 依 `CurrentUICulture` 對英文 UI 補上 `?lang=en` |

### 10.2 切塊策略

固定字數切塊（如 500 字）會截斷操作步驟，**不採用**。但依實測資料量，本案的「header-aware chunking」規模很小：

0. **先依 `lang` 屬性拆語系，再切塊——這是本案最容易靜默做錯的一步。**
   手冊的中英文**在同一份 HTML 裡並存**（§10.1），`display:none` 只是 CSS 視覺效果，
   對 `textContent` 完全沒有作用。直接抽純文字會得到
   「交期達成率Delivery Rate 路由：/DeliveryRate…Route: /DeliveryRate…」這種逐句中英交錯的字串——
   **embedding 會被稀釋、全文檢索的 `tsvector` 會混入兩種語言的詞、引文貼回使用者眼前是亂的**，
   而且三者都不會報錯。正確做法：抽文字時**保留兩份**——
   `[lang="en"]` 子樹去掉得中文版、`[lang="zh"]` 子樹去掉得英文版——
   兩份各自切塊、各自嵌入，chunk 加 `lang` 欄位；檢索時依提問語系（或 `CurrentUICulture`）過濾。
   一份 HTML 產生兩組 chunk，資料量加倍但仍極小（134 頁）。
1. **以 `<h2>` 為切點**；整頁文字量低於門檻者，**整頁作為單一 chunk**（中位數 5.9 KB HTML 約合 1.5–2.5 KB 純文字，多數頁面屬此類）。
2. 每個 chunk 前置「模組 / 畫面名」麵包屑（取自 h1 + h2）。這對專有名詞檢索的幫助大於切塊粒度本身。
3. **必須用正規 HTML 解析器**（AngleSharp / HtmlAgilityPack），不可用 regex——標題內含 `<i class="bi...">` 圖示元素，naive 抽取會得到空標題（實例：`wwwroot/docs/user/ord/delivery-rate.html` 的 `<h2>`）。
4. 欄位說明用的 `<table>` **不得從中間切開**。
5. chunk 保留圖片路徑，讓答覆能附上對應截圖——手冊有 121 張截圖，其資訊是純文字拿不到的。
   **英文 chunk 要存 `data-src-en` 的路徑而非 `src`**（`img/en/x.png`），否則英文答覆會附上一張中文介面的圖。
   注意英文截圖允許缺漏（頁面靠 `onerror` 退回中文圖），索引時同樣要做這層 fallback，不可存一個不存在的檔名。
6. chunk 儲存 `HelpDocResolver` 的路由鍵，引文連結即可免費產生（既有 [`HelpDocResolver`](../../../Services/HelpDocResolver.cs) 與 `HelpDocResolverTests` 已守門「新畫面必補手冊頁」，索引重建可掛同一份映射）。

### 10.3 版本控制與增量重建

- **以檔案內容 hash（SHA256）+ mtime 判斷變更，不設人工 `DocVersion` 欄位**。人工版號的宿命是沒人記得 bump；hash 差異驅動的增量重嵌是零維護的。
- 向量表欄位：`file_path`、`lang`（`zh`／`en`，見 §10.2-0）、`content_hash`、`chunk_index`、`heading_path`、`help_doc_key`、`embedding`、`indexed_at`。
  `content_hash` 取**整份 HTML**（雙語同檔），故一次變更會同時重嵌該檔的中英兩組 chunk——這是對的，
  兩種語言本來就一起改。
- 重建流程：掃描 → 比對 hash → 只重嵌變更檔的 chunk → 刪除已不存在檔案的 chunk。
- **重建作業會佔用 Ollama 推論資源**，須依 §十一.2 排到離峰或使用獨立限流。

### 10.4 語系與權限

- **手冊已中英雙語**（§10.1），原本「en-US 使用者只能拿到中文答案」的風險**已消失**：
  英文提問檢索英文 chunk、引文與截圖都是英文版，**不需要讓 8B 模型做翻譯**
  （原方案「直接回中文並標示」品質差且尷尬，現在不必採用）。待決 #4 因此改變性質，見下。
- **語系必須是檢索的過濾條件，不是排序的參考**。中英 chunk 混在同一個向量空間時，
  multilingual embedding 會讓「Sales Ranking」與「銷售排名」高度相似——
  看似貼心，實際結果是英文使用者拿到中文引文。以 `lang` 欄位硬過濾，別依賴模型自己分辨。
- **只有一個真正的邊界情況**：使用者以中文提問但 UI 是英文（或反之）。
  建議**以提問文字本身的語系為準**（CJK 字元佔比即可判定，不需要語言偵測套件），
  UI 語系僅作為判不出來時的預設值——人問中文卻拿到英文答案，比 UI 語系不一致更難接受。
- Embedding 模型必須 CJK-capable，**且必須是 multilingual**（同一顆模型要嵌中英兩份）。
  候選見 **§10.7**——初版定案的 `bge-m3` 出自北京智源，違反 §四 的來源限制已排除；
  剩下的候選都有前綴慣例，該陷阱改由程式與測試處理。
- **權限模型從簡**：手冊不含業務資料，只需依使用者的選單權限過濾檢索範圍（避免洩漏系統功能結構，屬低風險）。不套用 §八 的嚴格模型。
- **本節只涵蓋手冊語料。** 2026-08-06 新增的擷取物語料（`.md`／程式碼擷取而來的規則說明）
  走**完全不同的權限模型——開放查詢，安全邊界前移到索引前的內容審核**，見 **§10.11.5**。
  兩者的界線是「操作 vs 規則」而非權限高低；手冊維持本節的 MenuKey 過濾不變。
- 反向約束見 §8.7：**業務資料絕不可進入向量庫**。

### 10.5 混合檢索（**2026-08-05 實測更正：原方案的詞彙通道對中文完全失效**）

原文寫「pgvector 語意檢索 + PostgreSQL 全文檢索（`tsvector`）並行，以 Reciprocal Rank Fusion 融合」。
方向正確——手冊含大量畫面名稱／欄位代號等專有名詞，純語意檢索對這類詞彙表現不佳——
但**照字面實作的話，中文提問的詞彙通道會恆回 0 筆**。本機 PostgreSQL 16.14 實測：

```sql
select to_tsvector('simple','銷售排名報表可依客戶或品號彙總');
-- → '銷售排名報表可依客戶或品號彙總':1        整句是「一個」token

select to_tsvector('simple','銷售排名報表可依客戶或品號彙總')
       @@ plainto_tsquery('simple','銷售');
-- → f                                          查得到才有鬼
```

PostgreSQL 內建的分詞器對 CJK 沒有斷詞能力（沒有空白可切），整句話變成單一 lexeme，
只有**一字不差輸入整句**才命中。後果的形狀是最糟的一種：**不報錯、不拋例外、只是靜默退化成純語意檢索**，
而最需要精確命中的正是「銷售排名」「安全存量」「下角率」這類專有名詞——恰好是混合檢索存在的理由。

#### 修正：中文以**重疊雙字（bigram）**預切詞，英文不變

索引期與查詢期用同一個 C# 函式把 CJK 連續字串切成重疊雙字，再交給 `to_tsvector('simple', …)`。
實測可行（同一台機器）：

```sql
select to_tsvector('simple','銷售 售排 排名 名報 報表')
       @@ plainto_tsquery('simple','銷售');   -- → t   起首詞
select to_tsvector('simple','銷售 售排 排名 名報 報表')
       @@ plainto_tsquery('simple','排名');   -- → t   中段詞
```

| 決定 | 內容 |
|---|---|
| 中文 | 重疊雙字，`simple` 設定（不做 stemming，中文也沒有） |
| 英文 | 原文直接進 `english` 設定（有空白可切，stemming 有意義） |
| 單一 CJK 字的查詢 | 退化成前綴比對，不特別處理（手冊查詢幾乎不會只有一個字） |
| 存放 | chunk 另存 `lexemes` 欄（已切好的詞串），`tsvector` 由它產生 |

**不裝任何額外擴充**：`zhparser`／`pg_bigm` 都不需要（本機亦皆未安裝，只有 `pg_trgm` 1.6）。
這一步純粹是 C# 端十餘行的字串處理，且**必須索引期與查詢期共用同一個函式**——
兩邊切法不一致是這類設計的經典靜默失敗（兩邊各自看起來都對，只是永遠對不上）。

### 10.6 向量儲存：Phase 1 **不引入 pgvector**（2026-08-05 定案）

實測現況：本機 PostgreSQL 16.14 的 `pg_available_extensions` **沒有 `vector`**
（apt 有 `postgresql-16-pgvector 0.8.6-1.pgdg24.04+1` 可裝，但需 root 安裝 ＋ superuser 執行 `CREATE EXTENSION`）。

而依 §10.1 實測資料量推算的語料規模是：

| 項目 | 值 |
|---|---|
| 手冊頁數 | 134 頁（HTML 合計 1.81 MB） |
| 切塊後（中英各一份） | **563 chunks**（2026-08-05 實跑 `ManualDocParser` 量得，非估值） |
| 純文字總量 | 418 KB；每頁每語系中位 1191 字元，75% 的頁面整頁一塊 |
| 向量體積（1024 維 × float32） | **2.2 MB** |
| 精確餘弦 Top-K 的計算量 | 563 × 1024 ≈ 58 萬次乘加，**遠低於 1 ms** |

800 筆的 ANN 索引沒有任何效能可買，只有安裝成本要付。而 §8.7 已定案
**業務資料絕不可進入向量庫**，所以這個語料的規模**在結構上不會成長**（只隨手冊頁數線性增加）。

**決定**：embedding 存為一般 `real[]` 欄位，查詢時取出權限內的向量做精確餘弦，
介面切為 `IDocVectorStore`（`SearchAsync(float[] query, int topK, string lang, ISet<string> allowedMenuKeys)`）。
日後若真的長大，換一個 pgvector 實作即可，**不動呼叫端**。

> **2026-08-07 更正**：本段原寫「**啟動時載入記憶體**做精確檢索」，與實作不符。
> `DocVectorStore` **刻意沒有跨請求的向量快取**，理由寫在該類別的註解裡：
> 快取要處理失效（索引重建、換模型），省下的是幾十毫秒，
> 卻換來「手冊改了但答案還是舊的」這種查不出來的錯。
> **這個差異對「要不要裝 pgvector」這個問題是關鍵**——照原文讀會以為快取已經做了，
> 於是遇到規模問題時直接跳到 pgvector，而那階更便宜、也還沒用掉的手段根本還沒動。

#### 2026-08-07 以 S3 實測數據複核：結論不變，且現在有量

| 項目 | 實測值 |
|---|---|
| 語料 | 563 塊（zh 148／en 415），向量合計 **2.26 MB**（zh 595 kB、en 1,668 kB） |
| 每次提問取向量的 SQL | n=65，**min 1 ms／中位 2 ms／max 7 ms** |
| 同一批提問的生成耗時 | **41.5–267.6 秒/題**（六顆候選，CPU 推論，§10.8.2）；**定案的 `gemma4:latest` 為 52.8 秒/題** |
| 兩者比例 | 約 **1 : 26,000** |

檢索的資料庫成本是端到端延遲的 **萬分之零點幾**。裝 pgvector 是在最佳化這一段，
而使用者感受到的等待幾乎全部在生成。**沒有效能可買**這個原始判斷，現在不是推算而是量出來的。

成本面也沒變：本機 `pg_available_extensions` **至今仍無 `vector`**，
仍需 root `apt install` ＋ superuser `CREATE EXTENSION`，且是**每一個客戶部署都要做一次**。

**什麼時候該重新評估**（把原本「十萬筆以上」這個沒有依據的數字換成可檢查的條件）：
每次提問的傳輸量 ＝ 權限內塊數 × 維度 × 4 bytes（今天是 595 kB／2 ms）。
順序是 **① 先做帶明確失效機制的記憶體快取**（免費、不需擴充，代價是上面那段講的正確性風險）→
**② 真的裝不下記憶體、或需要次線性 ANN 時才輪到 pgvector**。跳過 ① 直接做 ② 是買貴的。

> **語料不會自己長大**：§8.7 已定案業務資料絕不進入向量庫，所以成長只來自手冊頁數與
> §10.11 的擷取物。即使擷取物把語料**翻倍**，也只是 4.5 MB／每次提問約 5–14 ms——不觸及任何門檻。

換到的東西：**pgvector 從安裝前置條件中整條移除**——少一次 root `apt install`、
少一次 superuser `CREATE EXTENSION`、少一整類「PG 沒裝 pgvector 就啟動失敗」的部署風險，
§17.5 的「已知落差（容器內 PG 裝不了 pgvector）」也連帶消失。

> **§17.3 拆 `QaDbContext` 的決定不變，但理由換了。**
> 原理由是「`CREATE EXTENSION vector` 會讓沒裝擴充的部署啟動失敗」——該風險已不存在。
> 新理由是：**選用功能的表不該在每個部署都建**，而且 `DocChunk` 會隨手冊重建而大量寫入／刪除，
> 與主 context 的業務資料生命週期完全不同。結論相同，`__EFMigrationsHistory_Qa` 照拆。

### 10.7 Embedding 模型：候選限於非中國來源，S2 實測後定案

> **本節 2026-08-05 改寫。** 初版定案 `bge-m3`，理由是「無前綴要求」。
> **該模型出自北京智源（BAAI），違反 §四 的硬性限制，已排除。**
> 這是本規格第一次因為**來源**而非技術理由撤掉一個決定，記在這裡當提醒：
> **模型名稱看不出組織歸屬，選型第一步是查來源，不是查跑分。**

#### 全候選盤點（2026-08-06 對 Ollama 官方庫查證）

Ollama 的 embedding 分類**總共只有 12 顆**，場地比想像中窄得多：

| 候選 | 來源 | 參數／維度 | 適用性 |
|---|---|---|---|
| **`embeddinggemma`** | **Google 🇺🇸** | 308M／768 | ✅ **現用**（定案 #28）。100+ 語言、專為 on-device RAG 設計 |
| **`granite-embedding:278m`** | **IBM 🇺🇸** | 278M／768 | ✅ 檢索導向、XLM-R 架構、含中文；**維度與現用相同，換用成本最低** |
| **`snowflake-arctic-embed2`** | **Snowflake 🇺🇸** | 568M／**1024** | ✅ 多語檢索；但**維度變大＝每題掃描量 +33%**，會提前觸發 §10.6 的重評 |
| **`nomic-embed-text-v2-moe`** | **Nomic AI 🇺🇸** | MoE／768 | ✅ 多語；MoE 在**純 CPU** 上的實際表現須實測，不可從參數量推斷 |
| `paraphrase-multilingual` | sentence-transformers 🇩🇪 | 278M／768 | ⚠️ 多語但為 **paraphrase 任務**訓練，非檢索導向。列為墊底對照組 |
| ~~`qwen3-embedding`~~ | ~~阿里巴巴 🇨🇳~~ | — | ❌ **§四 來源限制，出局** |
| ~~`bge-m3`／`bge-large`~~ | ~~北京智源 BAAI 🇨🇳~~ | — | ❌ **§四 來源限制，出局**（初版定案即為此，見本節開頭） |
| `nomic-embed-text`（v1） | Nomic AI 🇺🇸 | 137M／768 | ❌ **英文專用**，無多語訓練 |
| `mxbai-embed-large` | Mixedbread 🇩🇪 | 335M／1024 | ❌ 英文導向，CJK 表現不足 |
| `snowflake-arctic-embed`（v1） | Snowflake 🇺🇸 | 22–335M | ❌ 英文專用（v2 才是多語版） |
| `all-minilm` | sentence-transformers 🇩🇪 | 22／33M | ❌ 英文專用且過小 |

> ⚠️ **要避開的陷阱：`nomic-embed-text`（v1）是庫裡最紅的一顆（81.3M pulls），但它是英文專用。**
> 高人氣會讓它看起來像安全的預設值，對中文手冊卻是最差的選擇之一。
> **多語版本是另一顆模型**（`-v2-moe`），名字只差一個尾綴。

#### 前綴陷阱現在無法靠選型迴避，必須寫進程式並加測試

初版選 `bge-m3` 的理由正是「不必處理前綴」。**那個理由已經不存在**——
剩下的候選各自有 prompt／前綴慣例（e5 的 `query: `／`passage: `、arctic 的 query 前綴、
embeddinggemma 的 task prompt）。漏掉不會報錯，只會讓檢索品質默默下降。

→ **S2 的實作要求（三項，缺一即為未完成）**：

1. 前綴規則集中在 embedding client **同一個地方**，索引期與查詢期共用——
   與 `CjkTokenizer` 同樣的理由：兩份實作遲早對不上，而且無聲。
2. **各候選的實際前綴慣例於 S2 開工時逐一查證**（本規格刻意不預先斷言，
   避免把未驗證的細節寫成規格）。
3. 加一個對照測試：**同一段文字用「有前綴」與「無前綴」各嵌一次，斷言兩者的向量不同**。
   這條測試存在的唯一目的，是讓「前綴被拿掉」變成紅燈而不是無聲降級。

#### 查證結果（2026-08-06，`embeddinggemma` 實測）

三項要求皆已落地於 `OllamaClient.ApplyPrefix`（單一入口）＋ `OllamaClientTests` 的四條對照測試。
查證出來的事實有兩件，第二件比第一件重要：

**① Ollama 不會自動補前綴。** `/api/show` 顯示 `embeddinggemma` 的 template 是 `{{ .Prompt }}`
——**原樣送進模型**。所以前綴一定要呼叫端做，不能指望模型端處理。
（模型本身：307.58M 參數、BF16、768 維、`num_ctx` 2048、mean pooling。）

**② 但「有沒有加前綴」的差距，比預期小得多。** 以 S1 的 30 題題庫在**頁面層級**量四個方案：

| 方案 | query 前綴 | passage 前綴 | Recall@1 | MRR |
|---|---|---|---|---|
| **official** | `task: search result \| query: ` | `title: none \| text: ` | **30/30** | **1.000** |
| query_only | 同上 | 無 | 29/30 | 0.983 |
| swapped（對照組） | `title: none \| text: ` | `task: search result \| query: ` | 29/30 | 0.983 |
| none | 無 | 無 | 28/30 | 0.967 |

→ **採用 official**，但**採用的理由必須誠實寫清楚**：它是「量過之後不比別的差，而且是模型卡的慣例」，
**不是「量出了明顯優勢」**。差距只有兩題，而且**把前綴刻意用反（swapped）的分數與只加 query 前綴相同**——
這說明在這份語料上，*有沒有前綴* 的影響大於 *前綴是什麼*。

> ⚠️ **這組數字不可與 S1 的 Recall@1 60% 相比。** 這裡的檢索單位是**整頁**（268 份），
> S1 的評測是**塊**（563 塊），後者難得多。這一節量的是「四個前綴方案誰比較好」，
> 不是「語意通道有多準」——後者要等 S2-c 在塊層級與詞彙通道一起量。
> 落選的兩題也不是離譜的錯：`系統參數在哪個選單` 命中 `sys/index.html`（同模組目錄頁）、
> `outsource receipt scrap deduction` 命中 `rpt/outsource-performance.html`（相關報表）。

##### 三顆候選的前綴查證（2026-08-06，為 §10.7 比較而做）

`ApplyPrefix` 的原則是「表裡只放查證過的模型，未列的不加前綴」。要比較就必須先讓每顆
候選都用**自己模型卡的設定**，否則比出來的是「別人有前綴、它沒有」。查證結果：

| 模型 | Ollama template | query 前綴 | passage 前綴 |
|---|---|---|---|
| `granite-embedding:278m` | `{{ .Prompt }}` | 無（模型卡明示不需要） | 無 |
| `snowflake-arctic-embed2` | ⚠️ **Apache License 全文** | `query: ` | 無 |
| `nomic-embed-text-v2-moe` | `{{ .Prompt }}` | `search_query: ` | `search_document: ` |

> ⚠️ **`snowflake-arctic-embed2` 的 `template` 欄位裝的是授權條款全文，不是模板**——
> 打包疏失。這值得記下來是因為它的失敗形態很惡劣：若 `/api/embed` 會套用 template，
> 每一段文字都會被幾百字的法律文字淹沒，**相似度全部趨近 1，而且不報任何錯**。
> 實測否定了這個可能：無關兩句的餘弦是 **0.3346**（被污染的話會接近 1.0），
> 確認嵌入路徑不套 template。**這是第四次「模型 metadata ≠ 實際行為」**
> （前三次：前綴不自動補、`num_predict` 靜默回空、`context_length` 標示值是假的）。

**granite 有自己的分支而不是落進 `_`**，雖然兩者結果都是空字串：
`_` 的語意是「沒查證所以保守不加」，granite 是「查證過確實不用」，
後續處理完全不同。測試 `查證過不需要前綴_與_沒查證所以不加_必須分得開` 守的就是這件事。

**誠實標註**：這三顆的前綴取自模型卡，**未在本語料上做 A/B**（`embeddinggemma` 有做，見上表）。
對還沒被採用的候選先花一輪 A/B 前綴是浪費——真的有候選勝出，採用前再補量它的前綴。

#### 再評估的方法論問題：**現有題庫已飽和，量不出候選之間的差別**（2026-08-06）

上表列出四顆可用候選，但**不要拿現有的 30 題題庫去比它們**：`embeddinggemma` 在塊層級
已經是 **Recall@1 30/30、MRR 1.000**（§10.9 S2-c）——**指標打到天花板了**。
在一把已經滿分的尺上，任何候選最多打平，比較結果全是雜訊，
而「打平」會被誤讀成「換不換都一樣」。

→ **真正還有解析度的是 §10.9 S2-d 那個沒解決的分離度**：
正解語意分數 min **0.4094** vs 離題 max **0.4785**，**仍然重疊**，所以定案 #29 只能寫「不設門檻」。
**候選比較應該量這個，不是再比一次 Recall@k。** 若某顆候選能把重疊拉開，
等於順手解掉定案 #29 的遺留問題；若全部都拉不開，那也是一個該知道的結論
（代表問題不在模型，在「拒答」這件事本身要換做法）。

**前置條件**：這件事需要一批**難題**（跨頁、需推理、近義干擾），現有題庫太簡單。
該題庫同時也是待決 #5（生成模型選型）的前置——見 §14.2 待決 **#35**。

單輪成本：每顆 pull ＋ 重嵌 563 塊（308M 約 4 分、568M 約 8–10 分，純 CPU）＋ 30 題查詢；
四顆約一小時。`ApplyPrefix` 的 switch 已為每顆模型留好各自的前綴分支，**換模型不需改架構**。

#### 難題題庫：**53 題、三類、四個指標**（2026-08-06 建立並取得基準，解除待決 #35）

程式在 `SmartFactory.MES.IntegrationTests/QA/ManualHardBenchmarkTests.cs`，
與舊題庫**並存不取代**——舊題庫是回歸守門（有門檻值），新題庫是選型量尺（**刻意不設門檻**）。

| 類別 | 題數 | 針對的飽和來源 | 指標 |
|---|---:|---|---|
| **近義干擾** | 27 | 舊題庫每題只有一個合理答案，命中太容易 | 正解名次是否**壓過指定的干擾頁** |
| **需推理** | 14 | 舊題庫多半靠標題就能命中 | 正解 Recall@1／@3 |
| **跨頁** | 12 | 正解是一組頁面，Recall@1 對它沒有意義 | Coverage@5（去重成不同頁面後） |

**干擾頁是手冊裡本來就存在的近義對**，不是編出來的：`inv/inventory-valuation`（庫存評估表）
vs `inv/inventory-value`（庫存價值分析）、三種毛利（`gross-margin`／`margin-analysis`／
`contribution-margin`）、AP/AR 鏡像對、`mfg/schedule` vs `mfg/schedule-query`。
**正解由內文查證而非標題**——「月結要跟財報存貨科目核對」判給「庫存評估表」，
依據是它內文寫著「供月結與財報存貨科目核對」，而「庫存價值分析」寫的是「資金積壓分布」與
ABC 分析。**兩頁的標題比內文更像，這正是要量的東西。**

**基準（`embeddinggemma`，2026-08-06）**

| 指標 | 值 | 是否飽和 |
|---|---|---|
| 近義干擾勝出 | **26/27（96%）** | 接近飽和 |
| 需推理 Recall@1 | **9/14（64%）**、@3 10/14、MRR 0.721 | ❌ **沒飽和——最有解析度的一項** |
| 跨頁 Coverage@5 | **94%** | 接近飽和 |
| **分離度** | 正解 min **0.3224** vs 離題 max **0.5261** ＝ **−0.2037** | ❌ **沒飽和** |

唯一輸掉的干擾題是「客戶欠我們的錢逾期多久」→ 正解 `ar-aging` 排第 3、干擾 `ap-aging` 排第 1
（**應收應付被比反了**）。需推理輸的四題全是「答案藏在欄位說明的一句話裡」：
毛利成本口徑（第 9 名）、壞帳逾期天數算法（第 5）、發料成本時點（第 6）、
帳本明細截斷（第 9）。

**分離度比 S2-d 更難看，而這是刻意的。** S2-d 是 −0.0691（0.4094 vs 0.4785），
現在是 **−0.2037**，兩端各退一步：難題把正解下界從 0.4094 拉到 0.3224，
而離題樣本從 4 題擴到 12 題後上界從 0.4785 升到 0.5261。
新的上界由「**員工特休假怎麼請**」→ **特殊日**（0.5261）產生，
和舊上界「留職停薪→計劃停機」同一種病：**字形假朋友**。
> 一個由 4 題撐出來的門檻本來就不該拿來當安全依據。
> **樣本變難導致數字變差，不是退步，是先前的數字太樂觀。**

**這份題庫怎麼用**：換模型時比四個數字，優先看**沒飽和的兩項**（需推理、重疊量）。
兩項飽和的指標仍要看，但只作為「不得退步」的約束，不作為勝出依據。

> ⚠️ **更正（同日，四顆跑完後才發現）：上面那句「分離度」原本寫的是「絕對餘弦差」，
> 那個定義不能跨模型比，已改為下述的「重疊量」。** 見下一節。

##### 兩個會讓比較失真的實作細節（都已在程式裡處理）

1. **權限必須完全不是變因**：授權要授出**索引裡出現過的每一個 MenuKey**，不能只授正解那幾頁。
   只授正解的話干擾頁會被權限過濾掉，「正解壓過干擾」永遠 100%——一個由測試設定造出來的滿分。
2. **跨頁的 Coverage 要先把名次去重成不同頁面**：同一頁的多個塊各佔一個名次，
   不去重的話一頁吃掉 5 格就把涵蓋率壓成 1/3——那量到的是切塊粒度，不是檢索品質。

#### 候選比較：**四顆實測，並更正了本規格自己的指標定義**（2026-08-06）

**先講更正，因為它改變了結論。** 上一節原本把「分離度」定義為
**正解最低分 − 離題最高分**（絕對餘弦差）。四顆跑完後才看出這個定義壞在哪：

| 模型 | 全部分數的落點 | 絕對分離度 |
|---|---|---:|
| `embeddinggemma` | 0.27 – 0.74 | −0.2037 |
| `granite-embedding:278m` | **0.60 – 0.86** | −0.1143 |
| `snowflake-arctic-embed2` | 0.24 – 0.74 | −0.1219 |
| `nomic-embed-text-v2-moe` | 0.25 – 0.67 | −0.0946 |

**granite 的整個分佈都擠在高分區**（各向異性），同樣是「差 0.1」在它的尺上跟在
`embeddinggemma` 的尺上意義完全不同。**絕對餘弦差是有單位的量，而單位隨模型而變，
所以它可以同模型縱比、不可以跨模型橫比。**

→ 改用**名次性質、沒有單位**的量：**有幾題離題提問的分數高過正解的下界**。

還有第二個瑕疵，方向同樣是「讓差的模型好看」：
**正解沒進前 20 名時，它的分數根本不會進入樣本**。granite 的正解樣本是 **69/72**
（3 題全滅），於是它的下界是在**自己漏掉最難那幾題之後**算出來的——
**模型越差、樣本越乾淨、下界越高。** 兩個瑕疵都已修進程式（重疊量、樣本數與偏差警語）。

**實測結果**（53 題難題 ＋ 12 題離題，同一份索引、同一組權限，**兩輪結果完全一致**）

| 模型 | 來源 | 維度 | **重疊**↓ | **推理@1** | **推理@3** | 干擾 | 跨頁 |
|---|---|---:|---:|---:|---:|---:|---:|
| `embeddinggemma`（原定案 #28） | Google | 768 | 9/12 | 9/14 | 10/14 | 26/27 | **94%** |
| `granite-embedding:278m` | IBM | 768 | 10/12 | 8/14 | 9/14 | 24/27 | 75% |
| **`snowflake-arctic-embed2`** | Snowflake | 1024 | **3/12** | 9/14 | **13/14** | **27/27** | 92% |
| `nomic-embed-text-v2-moe` | Nomic AI | 768 | 7/12 | **11/14** | 12/14 | 26/27 | 85% |

**`snowflake-arctic-embed2` 在四項裡贏三項，且贏的幅度不是雜訊：**

- **重疊 3/12 vs 9/12** ——三倍差距。這一項直接對應定案 #29 那個訂不出來的門檻。
- **推理@3 13/14 vs 10/14** ——比 @1 更值得看：**S3 的生成模型拿到的是 top-K 塊，不是第 1 名**。
  `embeddinggemma` 錯的時候錯得遠（落在第 5／6／9／9 名），snowflake 唯一一次落在第 6 名。
- **干擾 27/27** ——四顆裡唯一全對。
- 唯一輸的是跨頁 92% vs 94%：12 題的 2 個百分點 ＝ **0.24 題**，是雜訊不是退步
  （相較之下 nomic 的 85% 是真的掉了）。

`granite-embedding` **四項全輸，直接淘汰**——它是「切換成本最低」的候選（同 768 維），
但切換成本低不構成理由。`nomic` 只在推理@1 領先，重疊與跨頁都輸，不足以抵消。

**代價（都已量過，都可接受）**：1024 維使掃描量從 1.7 MB 增為 **2.3 MB**
（仍遠低於 §10.6「每題掃描量 > 5 MB 才重評 pgvector」的門檻）；
模型檔 1.16 GB vs 622 MB；重嵌 563 塊約 8–10 分鐘 vs 4 分鐘（**只在重建索引時付**）。

> **仍未解決的**：snowflake 也沒有讓兩群分離（正解下界 0.3452 < 離題上界 0.4671），
> **定案 #29「不設硬門檻」維持不變**。而且四顆模型的離題上界**全都由同一題產生**——
> 「員工特休假怎麼請」比到「特殊日」。**換模型解不掉字形假朋友**，
> 這件事現在有四顆模型的證據，不再是單一觀察。

> **誠實標註**：題庫是自己出的 53 題，干擾與跨頁兩項的差距在樣本量下屬於雜訊，
> **決策依據只有重疊量與推理@3 這兩項**。

##### snowflake 的前綴 A/B（採用前補做，2026-08-06）

`embeddinggemma` 當初做過前綴 A/B，候選的前綴卻是直接取自模型卡。
**只在勝出者身上補這一輪**——對還沒被採用的候選先 A/B 是浪費：

| 方案 | 重疊↓ | 推理@1 | 推理@3 | 干擾 | 跨頁 |
|---|---:|---:|---:|---:|---:|
| **`query: `（模型卡）** | **3/12** | 9/14 | **13/14** | 27/27 | **92%** |
| 無前綴（對照組） | 4/12 | 9/14 | 11/14 | 27/27 | 87% |

前綴有效，四項裡三項變好、無一變差，方向一致——**這比 `embeddinggemma` 當初的結果乾淨**
（那次「把前綴刻意用反」與「只加 query 前綴」同分，代表前綴內容其實沒被學進去）。

**但這一輪真正的價值在對照組本身：`snowflake` 即使不加前綴（重疊 4/12），
仍然大幅贏過 `embeddinggemma` 加了官方前綴的結果（9/12）。**
所以勝出不是前綴造成的假象——**這是採用前必須確認的那件事**，
否則等於用「A 有前綴、B 沒有」的比較去換模型。

#### 截斷天花板：**標示值不等於實際行為**（2026-08-06 實測）

`/api/tags` 回報 `embeddinggemma` 的 `context_length: 2048`。**實際不是。**
量法：取一段文字並在尾端接一句識別句，看向量會不會變（cos 恰為 1.0 ＝ 尾句被丟棄）：

| 文字長度（CJK 字） | 4,000 | 6,000 | 8,000 | **12,000** | 16,000 |
|---|---|---|---|---|---|
| 加尾句後 cos | 0.9759 | 0.9713 | 0.9886 | **1.000000** | **1.000000** |

→ 真實天花板落在 **8,000–12,000 CJK 字之間**（未再細分），遠高於標示的 2048 tokens。

**對現有語料的結論：沒有任何一塊被截斷。** 實測 563 塊的文字長度中位數 563 字、
**最大 4,012 字**、超過 2,000 字的只有 12 塊——全部在安全區內。

**但這條天花板對 §10.11 的擷取物有效**：那些塊會比手冊長。
**切塊上限一律訂在 4,000 字以內**，該值有兩倍以上的安全邊際。

> 這是本規格第三次踩到「模型／執行環境的標示值與實際行為不一致」：
> ① 前綴（模型卡說有慣例，Ollama 的 template 不套用）、
> ② `num_predict` 設小會回空字串而非截斷的字串（§10.8.1）、
> ③ 本項。**選型與參數一律以實測為準，模型卡與 API metadata 只當線索。**

#### 不變的部分：換模型必須重嵌

**兩個必須存進表裡的欄位**：`EmbeddingModel`、`EmbeddingDim`。
不同模型的向量空間不可比，換模型後不重嵌，相似度會變成**雜訊而不是錯誤**（依然回得出 Top-K，只是全錯）。
→ 讀取時發現與現行組態不一致，**拒絕檢索並要求重建**，不做「盡量用」。
本次撤換 `bge-m3` 正好說明這兩個欄位不是理論需求：**選型真的會變。**

### 10.8 檢索不到時的行為（**門檻已實測：純詞彙通道訂不出來**）

檢索為空時，**明確回覆「使用手冊中查無相關說明」並附最接近的頁面連結**，
不把空的 context 丟給模型自由發揮。理由與 §9.2 相同：8B 模型在沒有依據時會流暢地編造操作步驟，
而使用者無從分辨。

原本還規劃了第二道「最高分低於門檻也視為查無」。**2026-08-05 以 S1 評測題庫實測，這道門檻在
只有詞彙通道時訂不出安全值**：

| 群體 | `ts_rank` 分數 |
|---|---|
| 30 題正解 | min **0.0144**、中位 0.0472、max 0.0904 |
| 4 題界外問題（「今天午餐吃什麼」等）的最高分 | min 0.0101、中位 0.0179、max **0.0259** |

兩群**重疊**（正解最低 0.0144 < 雜訊最高 0.0259）。任何門檻不是放過雜訊，就是砍掉真正的答案。
成因是 OR 召回的必然特徵：只要提問裡有**任一個**雙字出現在手冊（「保養」「週期」這類通用詞
到處都是），就會有命中。

→ **決定**：S1 只做「零命中＝查無」。分數門檻**延到 S2**，屆時以語意相似度為主判據
（語意分數對「離題」的區辨力遠高於詞彙分數），詞彙分數僅作輔助。
在那之前，寧可讓模型看到弱相關的 context 並依 §9.2 的提示詞說「手冊沒有寫」，
也不要用一個沒有依據的數字把正確答案擋掉。

> **連帶提醒（放大既有缺口 G2）**：手冊內容過期目前沒有任何測試抓得到。
> 現況下過期手冊只是沒人看；接上 Q&A 之後，它會被**很有自信地講出來**。
> 這不是 Phase 1 要解的問題，但要記在風險欄。
> （S1 已順帶補上一條相關守門：手冊內文的漢字若漏在 `lang` 標記外，
> 會同時顯示在中英兩版——實際揪出並修復了 `qcr/spc-analysis.html` 一處。）

### 10.8.1 生成參數的硬性契約（2026-08-05 對 `gemma4` 實測）

在開發機以 Ollama 0.32.5 ＋ `gemma4:latest`（**8.0B params、Q4_K_M、8.95 GB**）實測，
用的是索引裡真實的 `ord/sales-ranking.html` 中文 chunk 當 context。三個結果直接構成呼叫端的契約：

| # | 發現 | 影響 |
|---|---|---|
| **1** | **必須送 `"think": false`** | 這是思考型模型。開著思考：**528 tokens／43.4 秒**；關掉：**58 tokens／4.7 秒**——**同一個問題、同樣正確的答案，9 倍差距**。使用者等 44 秒與等 5 秒是兩種產品 |
| **2** | **`num_predict` 設太小會靜默回「空字串」** | 設 300 時：生成滿 300 tokens、`done_reason:"length"`，而 **`response` 與 `thinking` 兩個欄位都是空的**。推理過程吃光了額度，解析器等不到收尾，**答案一個字都沒有、也沒有任何錯誤**。前端會顯示一片空白 |
| **3** | 拒答行為正確 | 手冊未提及的問題（「可以匯出 Excel 嗎」「資料是即時還是隔夜批次」）兩題都回**「使用手冊中查無相關說明。」**，各 10 tokens／0.7 秒 |

**呼叫端因此必須做到三件事**（缺一都會在使用者面前壞掉）：

```jsonc
{
  "model": "...",
  "think": false,              // ① 不是效能微調，是可用性的前提
  "options": { "num_predict": 1024 }   // ② 寧可寬鬆
}
```

3. **收到 `done_reason == "length"` 或 `response` 為空時，一律視為失敗**，
   回一則明確訊息並記錄，**絕不把空字串當成答案送到畫面上**。

> 第 3 點是從第 2 點推出來的，而第 2 點是「靜默失敗」的教科書樣態：
> API 回 HTTP 200、`done: true`、token 也真的生成了，只有內容是空的。
> 沒有這道檢查，症狀會是「有時候問了沒反應」，而且完全無從查起。

**拒答那一項的連帶意義**：§10.8 原本擔心「檢索分數重疊 → 弱相關的 context 會讓模型硬掰」。
實測顯示模型在 context 不含答案時會照指示拒答，**風險比預期低**。
這不代表相關度門檻不必做，但它把門檻從「安全必需」降級為「體驗優化」——
與其擋掉正確答案，不如讓模型說它不知道。

### 10.8.2 提示詞契約與生成品質評測（2026-08-07，S3）

實作：`Modules/QA/Services/QaPromptBuilder.cs`（提示詞與解析器）、
`SmartFactory.MES.IntegrationTests/QA/ManualGenerationBenchmarkTests.cs`（量測）、
題目與檢索評測共用 `ManualQuestionBank`。

#### 一、答案的形狀先於答案的品質

提示詞強制模型在**最後一行**輸出固定格式的來源行：

```
來源：inv/stock-ledger.html、mfg/schedule.html
```

兩個理由，缺一都會出事：

1. **使用者要能點回手冊查證。** 8B 級模型在有 context 的情況下仍會把兩頁的內容揉成一段話，
   沒有引文連結時使用者無從分辨哪一句有依據。
2. **沒有固定格式就量不到品質。** 「答案有沒有依據」若只能人工判讀，
   待決 #5 的模型選型就只能靠印象。有了可解析的引文行，
   「引用的頁面是不是參考資料裡真的有的」與「引用的是不是正解頁」兩件事**都可以自動計分**。

**頁面代號用相對路徑而不是標題**——標題會被模型改寫（「庫存 › 庫存帳本」很容易被寫成
「庫存帳本查詢」），路徑則可以字串比對。這一條直接決定了評測能不能自動化。

**拒答句是固定字串**（`使用手冊中查無相關說明。`），不是「大意如此」。
呈現層據此決定不畫引文區塊，評測據此計分；讓模型自由發揮拒答用語的話，
兩邊都得退回模糊比對，而模糊比對會把「手冊沒有寫，但我猜是這樣」誤判成拒答——
那正是最危險的一種答案。

#### 二、提示詞第 2 條是實測補上的，不是想出來的

首版提示詞只寫「只能根據參考資料回答，找不到依據就說查無」。實測 `gemma4`：

| 提問 | 檢索給到的第 1／2 名 | 首版回覆 |
|---|---|---|
| 月結要跟財報的存貨科目核對金額，**該用哪張表** | `inv/inventory-valuation.html`（第 1） | 正確作答＋正確引文 |
| **想知道**資金積壓在哪些料、哪幾項佔的比重最大 | `inv/inventory-value.html`（第 1） | **使用手冊中查無相關說明。** |
| **要抓出**超過門檻天數都沒有進出的料 | `inv/slow-moving-inventory.html`（第 2） | **使用手冊中查無相關說明。** |

分界不是難度，是**句型**：寫成問句的答得出來，只說目的的一律拒答。
模型的理解沒有錯——手冊裡確實沒有「哪些料積壓最多」的答案；
錯的是提示詞沒講清楚**這個助理答的是「去哪裡看、怎麼操作」，不是「你的資料是多少」**。

> **這件事比它看起來重要**：若不補這一條就去比六顆模型，比到的會是
> **各家如何各自解讀我留下的歧義**，不是模型的品質。
> 而且結果會很好看——拒答率高的模型在離題題上是滿分。

補上第 2 條後，同樣四題：誤拒 2/4 → **0/4**、引文命中 2/4 → **4/4**。

**代價要一起記**：離題題「鸚鵡螺保養週期」原本正確拒答，補完之後被答成
「檢驗儀器的校正週期」。**拒答率與誤拒率是同一個旋鈕的兩端**，
放寬一端必然壓到另一端——所以這兩項在報表裡永遠並排印，不允許只看一項。

#### 三、四項指標刻意兩兩成對

| 指標 | 配對指標 | 只看一項會發生什麼 |
|---|---|---|
| **引文正確率** | **平均引用頁數** | 把參考資料五頁全部列進來源行，命中率必然好看——那不是理解，是把選擇題塗滿 |
| **拒答正確率**（離題該說不知道） | **誤拒率**（範圍內不該說不知道） | **一個永遠回「使用手冊中查無相關說明。」的模型會拿滿分** |
| **幻覺引文率** | — | 引用了參考資料裡根本沒出現的頁面代號；與答案內容無關，純粹是「有沒有在編」，完全可自動判定 |
| **繁中用語** | — | 簡體字＋大陸慣用語。使用者是台灣工廠現場人員，一句「请点击保存按钮」就足以讓整個功能不能用 |

**引文正確率必須拆分母**：檢索沒把正解頁放進參考資料時，模型再強也引不到——
那是檢索的失分，不是生成的。所以同時印出「檢索有給到正解」的題數，以及**條件於此**的命中率。
不拆的話，這個指標量到的是檢索與生成的混合物，而要換的是生成模型。

> **而那個分母要用「真的進了 prompt 的塊」，不是檢索回傳的 topK。**
> 超出字數預算被丟掉的塊（§10.8.2 的 `DefaultContextBudget`）模型根本沒看到，
> 卻會被算成「檢索有給到正解」，於是模型引不到就記成生成端的失分。
> 實測 chunk 平均 696 字、最長 1968 字，`topK=5` 撞到 6000 字預算是少數但確實會發生。
> 這個偏差對每顆模型方向相同（大家看到的 context 一樣），**不影響排名**，
> 但會讓「引文正確率」這個宣稱本身是錯的。
> 因此 `QaPromptBuilder.SelectWithinBudget` 是公開方法：
> 「模型看到了哪幾頁」是呼叫端必須知道的事，引文驗證與評測分母都用它。

> **繁中檢查刻意排除 后／干／只／里／系／制／于／采 這一類**——它們在繁體中同樣合法
> （皇后、干擾、只有、公里、關係、制度、于歸、采風），列進去會把正常的繁體回答判成簡體。
> **一個會誤報的品質指標比沒有指標更糟**：它會讓人去修根本沒壞的東西。

**本檔同樣不設回歸門檻**，理由與 §10.7 難題題庫相同：首測值當目標值是反模式。

#### 四、候選條件重訂（**2026-08-12 更正：原命題其實成立，是這裡的條件設錯了**）

待決 #5 原本寫的是「Breeze vs TAIDE，含 TAIDE 授權條款是否適合公開發布」。
2026-08-06 逐一查證 Ollama 官方 library：

| 名稱 | `ollama.com/library/…` |
|---|---|
| `breeze`／`breeze2`／`taide` | **HTTP 404，三個都不存在**（2026-08-12 重查仍是 404） |
| `llama3.2`／`mistral`／`phi4`／`granite4`（對照組） | HTTP 200 |

當時據此寫下「**拉不到的模型不能當預設值**，原命題因此失效」，候選改在
「library 拉得到 × 授權允許公開發布 × 非中國來源」三條件下重訂。

> ### ⚠️ 2026-08-12 更正：上面那個推論是錯的
>
> 404 是事實，「原命題失效」不是。**Ollama 從 0.3.13 起就能直接拉 HuggingFace 的 GGUF**
> （`ollama pull hf.co/<repo>:<quant>`），本機 Windows 端跑的是 **0.32.6**。
> 我把候選條件從「**拉得到**」窄化成「**官方 library 有**」，而這兩者從來就不等價。
>
> 真正的約束是規格自己寫的那條——**模型下載腳本不得需要任何 token**（§17.11.6）——
> 用它去篩，結論完全不同：
>
> | | Breeze | TAIDE |
> |---|---|---|
> | 官方 HF repo | `MediaTek-Research/Breeze-7B-Instruct-v1_0` | `taide/Llama3-TAIDE-LX-8B-Chat-Alpha1` |
> | 授權 | **Apache 2.0** | `license:other`（Llama3-TAIDE 社群授權同意書） |
> | gated | **否** | **是**（`gated=auto`；raw README 回 **401**） |
> | 未 gated 的 GGUF | 有，且同標 Apache 2.0 | 有，但都是**第三方對 gated 模型的再散布** |
> | 能不能當 setup 預設值 | **可以** | **不行**：官方 repo 要 token，鏡像的授權地位未查證 |
>
> 所以正確的說法是：**評估資格兩顆都有，setup 預設值資格只有 Breeze 有。**
> 這兩件事被我當初混成一件，於是連量都沒量就把兩顆刷掉了。
> 兩顆已於 2026-08-12 補測完畢（之十），結論不變但理由換了——
> **是實測輸掉，不是拉不到。**
>
> 方法上值得記的是失效模式：這是**用一個查得到答案的代理問題（library 有沒有）
> 去替換真正的問題（拉不拉得到）**，而代理問題的答案剛好是「沒有」，
> 於是整條路被關掉且看起來有憑有據。與 §10.9 S2-e 的量測漏掉 `Lang` 過濾同類——
> 都是**驗證的東西比要驗證的東西窄**。

**授權逐顆查證**（讀 Ollama registry 的 license blob，非讀說明頁）：

| 模型 | tag | 檔案大小 | 授權 | 出處 |
|---|---|---|---|---|
| Gemma 4 E4B | `gemma4:latest`（＝`gemma4:e4b`） | 9.61 GB | **Apache 2.0** | Google |
| Gemma 4 12B | `gemma4:12b` | 7.56 GB | Apache 2.0 | Google |
| Llama 3.1 8B | `llama3.1:8b` | 4.92 GB | Llama 3.1 Community License | Meta |
| Granite 4 Tiny-H | `granite4:tiny-h` | 4.23 GB | Apache 2.0 | IBM |
| Mistral 7B | `mistral:7b` | 4.37 GB | Apache 2.0 | Mistral AI |
| Phi-4 14B | `phi4:latest` | 9.05 GB | MIT | Microsoft |

**因授權排除**（都拉得到、也都不是中國來源，純粹是條款不允許）：

| 模型 | 大小 | 授權 |
|---|---|---|
| `aya-expanse:8b` | 5.06 GB | **CC-BY-NC 4.0**（Cohere） |
| `command-r7b` | 5.06 GB | **CC-BY-NC 4.0** ＋ Acceptable Use Addendum |
| `exaone3.5:7.8b` | 4.77 GB | **EXAONE AI Model License 1.1-NC**（LG） |

> `falcon3:7b`（4.57 GB、Falcon 3 TII License，條款自述「部分基於 Apache 2.0」但另附使用政策）
> 屬邊界案例，本輪未測；若日後要用需先讓法務看過 AUP。

**兩個順帶查到、與現行設定直接相關的事實**：

1. **Gemma 4 改用 Apache 2.0。** Gemma 3 仍是 `Gemma Terms of Use`（`gemma3:4b` 的 license blob
   8,432 字），Gemma 4 是 Apache 2.0 全文（11,355 字）。已與 HuggingFace `google/gemma-4-*`
   全系列標記 `license:apache-2.0` 交叉查證——**兩個來源一致**。
   這一則本來被列為「Ollama metadata 不可信」的待查項（同日 `snowflake-arctic-embed2` 的
   `template` 欄位裝著 Apache 授權全文，是打包疏失），查證結果是**這次 metadata 沒錯**。
   待決 #5 的授權疑慮因此消掉一半。
2. **現行設定是「用比較大的檔案跑比較小的文字模型」。** `gemma4:latest` 就是 `gemma4:e4b`
   （有效 4.5B），9.61 GB；而 `gemma4:12b` 是完整 12B 稠密模型，**只有 7.56 GB**。
   差額是 E 系列帶著的視覺（16 blocks）與音訊（12 blocks）權重——本功能一個都用不到。

#### 五、六顆候選的實測結果（2026-08-07）

同一份題庫（53 題難題 ＋ 12 題離題）、同一個提示詞、同一顆嵌入模型
（`snowflake-arctic-embed2`）、`topK=5`。**檢索端對每顆模型完全相同**——
六輪的「檢索有給到正解」都是 52/53，所以引文欄位比的確實是生成。

| 生成模型 | 引文命中<br>（給到正解時） | 沒附<br>來源 | 幻覺<br>引文 | 離題<br>拒答 | 誤拒 | 繁中<br>缺陷 | 生成<br>失敗 | 秒/題 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| **`gemma4:latest`（e4b）** | **50/52（96%）** | **0** | **0** | **11/12** | **0/53** | **0** | **0** | 52.8 |
| `gemma4:12b` | 44/52（85%） | **0** | **0** | **11/12** | **0/53** | 0＊ | 4 | 115.6 |
| `mistral:7b` | 34/52（65%） | 3 | 4 | 4/12 | 12/53 | 1＊ | 2 | 116.9 |
| `llama3.1:8b` | 27/52（52%） | 22 | 4 | 5/12 | **0/53** | 0＊ | 0 | 76.0 |
| `granite4:tiny-h` | 20/52（38%） | 19 | 15 | 8/12 | 6/53 | 5＊ | 0 | 41.5 |
| `phi4:latest`†  | 49/52（94%） | **0** | **0** | 10/12 | 1/53 | 2 | 1† | 267.6 |
| **Breeze-7B-Instruct-v1_0**‡ | 15/52（29%） | 24 | 5 | 6/12 | 4/53 | **0** | **0** | 68.3 |
| **Llama3-TAIDE-LX-8B-Chat-Alpha1**†‡ | 10/52（19%） | 29 | 16 | **0/12** | 0/53※ | **0** | 1† | 135.7 |

> ＊ 繁中欄已扣除檢查器誤報，見下方「量錯的那一項」。原始輸出分別是
> 12b／mistral／llama3.1／granite4 ＝ 1／3／1／7。
> † phi4 與 TAIDE 的量測條件與其餘六顆不同（`TimeoutSeconds=900` 而非出貨預設的 180），
> **生成失敗欄不可與它們並列比較**——兩者那 1 題都是撞 `NumPredict=1024` 截斷，不是逾時。
> ‡ 這兩顆不在 Ollama 官方 library，走 `ollama pull hf.co/…` 直拉 GGUF，見下方之十。
> ※ **TAIDE 的「誤拒 0/53」不是優點，是同一個退化的另一面**：它離題拒答也是 0/12，
> 亦即**它從不說「查無」**。§10.8.2 之三把拒答與誤拒設計成必須成對讀，就是為了擋這種讀法。

**`gemma4:latest` 在每一項都是最好或並列最好**，沒有任何一項需要權衡。定案照此。

##### `phi4:latest`：唯一需要換條件才量得到的一顆

**它在 `TimeoutSeconds=180`（出貨預設值）下 65 題全數逾時。**
這不是管線壞掉——直接量一次真實大小的請求：prompt 3,914 tokens、生成 413 tokens、
**總計 280.4 秒**，超出預設值 56%。也就是說**這顆模型配上本專案的預設組態產不出任何答案**。

但依 §17.11.3，延遲是環境產物（開發機無獨顯、一律 CPU），**不得作為評選依據**——
用 180 秒把它判 0 分，等於在用延遲評選。故改以 `TimeoutSeconds=900` 重跑取其品質數字
（2026-08-11，第四輪；前三輪的作廢原因見之九）。兩件事要分開記：
**①** 它在預設組態下不可用（功能事實，與硬體綁定）；**②** 它的答案品質，即上表那一列。

**給了它公平條件之後，它仍然每一項都不如 `gemma4:latest`**——
引文 49/52 對 50/52、拒答 10/12 對 11/12、誤拒 1/53 對 0/53、繁中缺陷 2 對 0、
生成失敗 1 對 0。**沒有任何一項是 phi4 勝出的**，所以定案不需要在「品質換延遲」之間權衡：
它是在自己最寬鬆的條件下輸給對手最嚴格的條件。

兩個繁中缺陷都是真的（檢查器已於之七修好，不是誤報）：一題把「資訊」寫成**「信息」**
（中國用語），另一題「出差的交通費怎麼報銷」**整段用英文作答**（漢字 11／拉丁字母 105）。
後者尤其致命——這是一個中文使用手冊的問答功能。

> **順帶量到一件事：phi4 每題 267.6 秒，是 `gemma4:latest`（52.8 秒）的五倍。**
> 依 §17.11.3 這不列入評選，但它解釋了為什麼這一輪要跑 4.5 小時而前三輪只跑 1.86 小時——
> 前三輪從第 11 題起檢索被打壞、只拿得到目錄頁，**上下文短所以生得快**。
> 「這輪怎麼比上輪慢這麼多」在當時就該是個警訊，卻被當成雜訊放過（見之九）。

#### 六、幾個只有並排讀才看得出來的東西

**① mistral 的拒答決定接近丟銅板。** 它同時是**誤拒最多**（12/53＝23%）
與**離題拒答最少**（4/12＝33%）。兩個比率幾乎一樣，等於「要不要拒答」
與「答案在不在參考資料裡」幾乎無關。同一組數字在 `gemma4:latest` 是 0% 對 92%。
**任一項單看，mistral 都不會長這麼難看**——這正是當初堅持兩項並排印的理由。

**② llama3.1 的主要失分不是不懂，是不照格式。** 53 題裡有 **22 題根本沒吐來源行**。
它那 4 次幻覺引文的形狀也很說明問題：`schedule.html`（掉了模組前綴）、
`ecl/act-ecl.html`（路徑前後顛倒）、`ARAging.html`（自己造一個 PascalCase 檔名）——
全是「憑印象補一個看起來很合理但不存在的頁面」，正是提示詞第 6 條要擋的東西。

**③ 同家族的大模型更差也更慢。** `gemma4:12b` 是完整 12B 稠密模型、檔案還比較小
（7.56 GB vs 9.61 GB），引文卻是 44/52 對 50/52，而且**有 4 題撞到 180 秒逾時**。
「換大一點的應該會更好」在這個任務上不成立，實測才知道。

**④ 延遲與檔案大小無關。** `llama3.1:8b`（4.92 GB）每題 76 秒，比 9.61 GB 的
`gemma4:latest`（52.8 秒）還慢——因為後者是 E4B，**有效參數只有 4.5B**，
檔案大是帶著本功能用不到的視覺與音訊權重（§10.8.2 之四）。
延遲照 §17.11.3 不列入評選，但這解釋了為什麼不能拿檔案大小當效能的代理指標。

**⑤ `num_predict` 的邊界比想像中近。** granite4 平均生成 242 tokens、**最長 891**，
上限是 1024。沒有任何一顆因此回空字串（§10.8.1 第 2 點的靜默失敗沒被踩到），
但 891/1024 這個距離值得記著——換成更囉嗦的模型或更長的 context 就會撞上。
`mistral` 與 `gemma4:12b` 的 6 次生成失敗全是 **180 秒逾時**，不是截斷。

#### 七、量錯的那一項：繁中檢查器自己會誤報

首輪的「非中文作答」用的是「漢字數 × 4 < 總長度」。於是
**「庫齡固定分成幾段」這一題在五顆模型裡有四顆被判非中文作答**——
而該題的正確答案本來就是 `0–30、31–60、61–90、91–180、181–360、360+ 六段`
（已核對 `inv/inventory-aging.html` 原文），整句幾乎都是數字。

> **同一題跨模型全中，原因在資料不在模型。** 這與 embedding 選型時
> 「員工特休假」被比到「特殊日」是同一種形狀：四顆模型的離題上界全由同一題產生。
> 遇到這個形狀，先去看那一題，不要先怪模型。

本節上面自己寫著「一個會誤報的品質指標比沒有指標更糟」，而那句話底下就掛著一個。
**修法**：分母從「總長度」換成**拉丁字母數**，並要求至少 24 個拉丁字母才判定
（免得 `OEE`／`MRP`／`AVCO` 這類專有名詞觸發）。檢查器抽成
`ChineseTextFlaws`（不需 DB、不需推論服務的純字串判定）＋13 項測試，
**第一組測試全部是當天真的誤報過的字串**。

**修正後仍有兩筆無法判定**：`granite4` 的「出差的交通費怎麼報銷」（漢字 13/141）
與 `mistral` 的「MRP 是逐批下還是有最小批量」（漢字 7/30）。首輪沒開 dump，
答案原文沒有留下來，而重跑六顆要再七小時。**排序不受影響**
（`gemma4:latest` 兩種算法都是 0，且其餘每一項都領先），故不重跑；
`phi4` 那一輪已開 dump，其繁中判定可逐句查證。

#### 八之前：一條比這張表活得久的選型準則

> **為某個特性選模型之前，先確認那個特性的指標還沒飽和。**
>
> 這條是 2026-08-12 補測 Breeze 與 TAIDE（之十）換來的。當時測它們的理由是
> 「台灣在地模型，繁中應該較好」，而繁中確實是六項指標之一——**看起來完全站得住**。
> 結果兩顆的繁中缺陷都是 0，卻因此**贏不了任何人**：`gemma4:latest`、`gemma4:12b`、
> `llama3.1:8b` 本來就都是 0。**主場優勢真實存在，但那個欄位早就沒有分可以拿了。**
>
> 這個錯誤不會在結果裡自己現形——飽和的指標長得跟「並列最好」一模一樣。
> 唯一擋得住它的是動手之前先去看該指標**現有候選的分布**：
> 若既有的人已經全拿滿分，那條理由就不成立，該換一個真的還有差距的指標來選。
>
> 與 §10.7「再評估的方法論問題」同源——那次是**檢索**指標飽和到分不出高下，
> 這次是**繁中**指標飽和到選不出理由。**同一個陷阱，換一個欄位。**

#### 八、讀這份結果時要記得的三個限制

1. **比的是「同一個提示詞下」的差異。** §10.8.2 之二那個發現說明了，
   只要提示詞留下歧義，換模型量到的就是各家如何解讀那個歧義。
   這份表能用來**選模型**，不能用來說「這就是這顆模型的能力上限」。
2. **`mistral` 與 `gemma4:12b` 的分母略微不利。** 生成失敗的題目仍留在 52 的分母裡
   （分母在生成前就記了）。扣掉的話 mistral 是 34/50、12b 是 44/48。
   不影響排序，但要知道這個瑕疵在。**沒有跑到一半改 harness**——
   那會讓前後幾顆不可比。
3. **繁中欄的兩種算法混用**：五顆用首輪（含誤報）、`phi4` 用修正後的檢查器。
   表中的五顆已手動扣除可確認的誤報，兩筆無法判定者標示於上。

#### 九、毀掉兩輪量測的，不在量測裡（2026-08-10）

`phi4` 那一列跑了四次才拿到能用的數字。前兩次失敗與模型無關，也與 harness 無關：
**是另一個工作區的測試把量測的前提改掉了。**

露餡的是一個不該變動的數字。五顆有效候選的「檢索有給到正解」全都是 52/53——
檢索端對每顆模型完全相同，這個值本來就不該動。而那兩輪分別印出 **1/53** 與 **10/53**，
同時**引文命中率反而更好看**（1/1、10/10）。

> **這是這份評測最該記住的一件事：前提壞掉時，品質指標不會變難看，會變好看。**
> 因為分母跟著一起縮小了。一個只印「引文命中 100%」的報表，
> 在管線最壞的狀態下長得最漂亮。

**根因**（用 `AuditLogs` 查的，不是猜的）。`MenuPermission` 的每一次異動都留有前後值：

```sql
select date_trunc('second',"Timestamp" at time zone 'Asia/Taipei') ts,
       "Changes" like '%"After":{"CanRead":true}%' as to_true, count(*)
from "AuditLogs" where "EntityType"='MenuPermission' group by 1,2 order by 1;
```

11:20 與 13:55 各有一次 148 筆 `CanRead: true → false`，剛好落在兩輪量測的中途。
拿這兩個時刻去比對各 session 的 `dotnet test` 呼叫，對上的是
`ManualIndexIntegrationTests`——它的權限測試會把角色可讀鍵收窄到只剩一兩把且不還原
（**那正是它要測的東西，本身沒有錯**），錯在它收窄的是 Admin。
殘留形狀也認得出來：`CanRead=false` 而 `CanCreate/CanPrint` 原封不動，
只有「逐列改 `CanRead`」那一種寫法做得出來。

**為什麼隔離不了**：`TestWebApplicationFactory` 把測試庫名寫死成 `SmartFactoryMES_Test`。
`DisableTestParallelization` 只序列化**同一進程內**的 collection；
對「不同 git worktree、不同 `dotnet test` 進程」零保護。隔離的是程式碼，不是資料庫。

**三層修正**（各擋不同的東西，缺一不可）：

| 層 | 做法 | 擋掉什麼 |
|---|---|---|
| 肇因 | 收窄型權限測試改用**專屬角色** `QA-INDEX-TEST` | 不再有人動別人依賴的 Admin |
| 偵測 | 量測迴圈**每 5 題**重驗前提，壞了直接宣告該輪作廢 | 把「兩小時後才知道白跑」縮成幾分鐘 |
| 結構 | 幾小時的量測用**自己的資料庫**（`ConnectionStrings__DefaultConnection` 覆寫） | 不必仰賴別人配合 |

> 專屬角色有兩個實作陷阱：`MenuPermissions.RoleId` 有 FK 指向 `AspNetRoles`，
> 角色必須真的建一列；而新角色一開始沒有任何權限列，
> `allowAll` 會變成「什麼都給不到」，得先依索引裡實際出現過的 MenuKey 補齊列。

**還原不是解法。** 「跑完把權限改回去」擋不住「跑到一半被另一個進程讀到」——
被害的那一輪照樣拿到壞資料，而且還原後現場就消失了，更難查。

#### 十、補測台灣在地模型：Breeze 與 TAIDE（2026-08-12，第七、八輪）

使用者質疑「Breeze 及 TAIDE 應該在 ollama 中可以使用」——**質疑成立**，見之四的更正。
兩顆補測完畢，同一份 53 題難題＋12 題離題、同提示詞、同嵌入模型、`topK=5`。

| | Breeze | TAIDE |
|---|---|---|
| tag | `hf.co/YC-Chen/Breeze-7B-Instruct-v1_0-GGUF:Q4_K_M` | `hf.co/QuantFactory/Llama3-TAIDE-LX-8B-Chat-Alpha1-GGUF:Q4_K_M` |
| 大小／參數 | 4.54 GB／7.49B | 4.92 GB／8.03B |
| 基底 | Mistral-7B（2024） | Llama-3-8B（2024） |
| chat template | Mistral `[INST]` ✓ | Llama-3 `<\|start_header_id\|>` ✓ |
| 吃 system prompt | ✓ 實測 | ✓ 實測 |

> **社群轉檔的 GGUF 必須先驗 template 再投入整輪。** ollama 從 GGUF metadata 自動推導
> Modelfile，推錯就是整輪白跑；Breeze 拿到的是 Mistral **v3** 的 `[SYSTEM_PROMPT]` 標記，
> 而 Breeze v1_0 訓練時是舊格式——**這種不一致會讓比較變得不公平而非只是變差**。
> 故兩顆都先單獨驗「給一條 system 指令會不會照做」，都通過才開跑。

**結果：兩顆都大幅落後，且落後的方式各不相同。**

- **Breeze**：引文 15/52（29%），零逾時、零生成失敗，但 **53 題裡 24 題根本沒附來源行**，
  平均引用僅 0.61 頁。不是引錯，是多數時候不引。
- **TAIDE**：引文 10/52（19%），**離題拒答 0/12**——它從不說「查無」。
  「怎麼申請留職停薪」「員工特休假怎麼請」「出差交通費怎麼報銷」全部被它編出
  一二三步驟的操作指引，而手冊裡沒有人事差勤模組。**這是最危險的一種失效**：
  自信、格式正確、內容純屬虛構。它的「誤拒 0/53」因此毫無意義——
  §10.8.2 之三把這兩項設計成必須成對讀，正是為了讓這種退化無所遁形。

##### 這一輪最該記的不是名次

**兩顆的繁中缺陷都是 0——但那贏不了任何人**，因為 `gemma4:latest`、`gemma4:12b`、
`llama3.1:8b` 本來就都是 0。**台灣在地模型被期待的主場優勢，在這個題庫上指標早已飽和**，
所以兌現不出來。這比「它們名次低」更關鍵：它說明「為了繁中而值得一測」這個理由本身站不住，
真正拉開差距的是引文紀律。**下次要為某個特性選模型之前，先確認那個特性的指標還沒飽和。**

##### 一個真的缺陷：提示詞的範例頁碼會被照抄

`QaPromptBuilder` 的格式說明寫著「頁面代號請原樣抄寫……（例如 `inv/stock-ledger.html`）」，
而弱模型會把**範例本身**當成引文抄出來：

| 模型 | 幻覺引文 | 其中指向 `inv/stock-ledger.html` |
|---|---:|---:|
| Breeze | 5 | **4（80%）** |
| TAIDE | 16 | **10（63%）** |
| `granite4:tiny-h` | 15 | 0 |
| `mistral:7b` | 4 | 0 |
| `llama3.1:8b` | 4 | 1 |
| `gemma4` ×2、`phi4` | 0 | 0 |

**注意 granite4 幻覺 15 次卻一次都沒抄範例**——所以這不是「所有模型共通的提示詞 bug」，
而是**提示詞提供了一個踩法，只有這兩顆踩**。這不改變名次——全部扣掉後
Breeze 仍是 15/52、TAIDE 仍是 10/52。

> ⚠️ **本節當時的處置建議（把範例換成明顯不存在的 `xxx/yyy.html`）已於 2026-08-13 被實測推翻，
> 提示詞維持原樣未改。** 見之十一。

##### TAIDE 第一輪作廢：又是 180 秒

TAIDE 首輪 20 題「生成失敗」**全部是 `TimeoutSeconds=180` 逾時**，不是截斷也不是空回覆
（平均 135.7 秒／題、平均生成 396 tokens，話多，整條分布壓在牆上）。
與 phi4 同一種失效，故**比照 phi4 放寬到 900 秒重跑**，首輪數字作廢。
只給 phi4 放寬而不給 TAIDE，比較就不公平。

> **附帶一個未解釋但已收斂的異常**：作廢那一輪的檢索分母是 **51/53**，
> 其餘七輪都是 52/53。已排除三種可能——`retrievalGave++` 在失敗 `continue` **之前**
> （不是被自己的逾時灌水）；同一句連續嵌入三次**位元完全相同**（不是浮點抖動）；
> 同期跑的純檢索基準近義干擾 27/27（權限與索引沒崩）。
> 重跑後回到 52，與其餘七輪一致。**51 只出現在那唯一一輪，且該輪同時有 20 次逾時。**
> 機制未證實，但該輪已作廢、不進表，故不再追。記在這裡是因為
> **「分母無故變動」本身就該留下痕跡**，下次若再出現就有兩個樣本可比。

#### 十一、想修掉那個範例，三輪全部退步——提示詞維持原樣（2026-08-13）

之十發現「弱模型會把提示詞第 6 條的範例 `inv/stock-ledger.html` 當引文抄出來」，
並順手建議改成不存在的頁碼。**照著改，量出來三個版本全部比原版差**，故**不改**。

同一顆 `gemma4:latest`、同題庫、同嵌入模型、`topK=5`、`Temperature=0.2` 無固定 seed，
每輪約 46–51 分鐘，只改第 6 條那一句：

| 第 6 條的寫法 | 引文(給到正解時) | 幻覺引文 | 拒答 | 誤拒 | 繁中 |
|---|---:|---:|---:|---:|---:|
| **v0 原版**：例如 `inv/stock-ledger.html` | **50/52** | **0** | 11/12 | 0/53 | 0 |
| v1：改文字描述「格式長這樣：模組代碼/檔名.html」 | 50/52 | 1 | 11/12 | 0/53 | 0 |
| v2：v1 ＋ 明寫「中括號後面的標題不要抄進來」 | 50/52 | 1 | 11/12 | 0/53 | 0 |
| v3：假頁面具體字面「長得像 `xxx/yyy.html`」 | 49/52 | 1 | 11/12 | 0/53 | 0 |

**v1/v2/v3 的失分是同一題、同樣的 token**——「從 MRP 展開到工單開工要經過哪些畫面」，
模型把整段開頭 `[tutor/index.html] 操作教學 › 製造 › MFG` 連標題一起抄進來源行，
而 `QaPromptBuilder.Parse`（`QaPromptBuilder.cs:188`）以空白為分隔字元之一，於是裂成
`tutor/index.html`、`操作教學`、`›`、`製造`、`MFG` 五筆，後四筆計為幻覺引文。
這一題在 v0 是答對的。

##### 兩個假說依序被自己的實測否定

1. **「原範例的作用是劃出抄寫邊界」** → v2 把邊界寫成明話，`幻覺` 仍是 1，
   失分題與 token 完全相同，**兩輪重現**（非取樣抖動）。否定。
2. **「模型是在對齊一個具體字面，不是在讀規則，所以假頁面也行」** → v3 給了具體字面，
   `幻覺` 仍是 1，且 `引文` 還掉了 1 分。否定。

**有效的只有那個真頁面本身，機制未明。** 到此停手：再調下去是在拿 46 分鐘一輪的
黑箱實驗湊指標，而**待修的缺陷只發生在已經淘汰的模型上**（見下）。

##### 為什麼「不修」是正確的取捨，而不是擱置

那個缺陷的受害者是 Breeze 與 TAIDE——**兩顆都因為引文紀律太差而未入選**（15/52、10/52）。
對正式採用的 `gemma4:latest`，原版提示詞在六項指標上**嚴格優於或等於**三個修正版。
為了兩顆不會上線的模型去換掉一個對上線模型更好的提示詞，方向是反的。

> **但這是「已知風險」而非「非風險」，換生成模型時必須重驗**：
> 提示詞裡的真實頁碼等於遞給模型一個可以充數的真答案，而使用者**點得下去**，
> 會落到一個真實但錯誤的頁——比明顯亂碼更難被察覺。
> 換模型後若 `幻覺引文 > 0`，第一件事是 grep 結果裡有沒有 `inv/stock-ledger.html`。

##### 一條方法論

**改提示詞必須整輪重量，而且要盯住沒被改動的那幾項。** 三輪裡 `拒答 11/12`、
`誤拒 0/53`、`繁中 0` 從頭到尾沒動，唯一動的是 `幻覺引文`；若只看「有沒有變好」
會誤判 v1「沒退步」。另外，`引錯頁` 明細本身有 ±1 的輪間抖動
（v2 多了 `mobile/preparation.html`、少了 `osp/index.html`），
而 `引文 50/52` 三輪一致——**逐題明細不能單獨拿來下結論，要看聚合指標**。

#### 十二、擷取物語料上線會讓上面那張表的前提失效（2026-08-14）

**這一節不是要改之五的數字，是要記下那些數字什麼時候不再可比。**
問題是「模型選型評估是否須更新」時查證出來的：沒有任何新事實動搖排名
（`gemma4:latest` 仍是每一項最好或並列最好），**但擷取物語料一旦核可進索引，
量測的前提會壞掉兩處，而壞掉的方向是「數字變難看而模型沒變差」。**

| # | 會壞的東西 | 為什麼 |
|---|---|---|
| ① | **`q.Expected` 只寫了手冊頁代號** | 題庫建立時擷取物不存在。而擷取物存在的**理由**正是「手冊答不出的規則類問題」——模型引 `rules/currency-04.html` 是答對了，`ManualGenerationBenchmarkTests` 會記成「引錯頁」 |
| ② | **「檢索有給到正解 52/53」這個錨會自己動** | `topK=5` 加字數預算是固定的，擷取物塊會跟手冊塊搶那五個位置。而之九抓到權限被別的進程改掉，靠的正是「這個值對每顆模型都相同、本來就不該動」 |

**② 比 ① 嚴重。** ① 只是某幾題失分，看得出來；② 是**偵測機制本身失效**——
錨會動之後，前提再被污染一次就沒有東西會露餡了。

> 與之九互為鏡像，值得並排記：
> **之九是前提壞掉、數字變好看**（分母跟著縮小）；
> **這次是前提沒壞、數字變難看**（語料變多）。兩種都會騙人，而且騙的方向相反。

**一件查證過、不會壞的**：幻覺引文的判定比的是 `given`——真的進了 prompt 的頁面代號——
不是磁碟上有沒有那個檔（`ManualGenerationBenchmarkTests` 第 179 行）。
所以 `rules/<slug>.html` 進來之後不會被誤判成幻覺。**記下來是為了擋掉「順手修一下」。**

**處置：不重跑任何選型量測。** 第一批擷取物核可、要進索引**之前**，
先跑一次同模型的「有擷取物 vs 無擷取物」對照，**只跑 `gemma4:latest` 一顆**——
那是在量語料變更，不是在量模型，跑第二顆沒有任何意義。
同時把 `q.Expected` 補上規則類題目該引的 `rules/` 頁碼。

##### 對照量測實跑（2026-08-17，`ManualExtractImpactBenchmarkTests`）

`gemma4:latest`／`snowflake-arctic-embed2`／`topK=5`，兩臂在同一進程內依序跑完
（先「無」後「有」，理由見該測試的類別註解），語料為 13 份 **471 則、已核可 264 則**，
耗時 1 小時 40 分。**跑在專用庫 `SmartFactoryMES_QaBench` 而不是共用測試庫**：
本測試的 `GrantEveryIndexedKeyAsync` 會把角色 1 的 `MenuPermissions` 全開，
而共用庫的庫名是寫死的、跨進程零保護——之九那次污染就是這樣來的。

| | 無擷取物 | 有擷取物 | |
|---|---|---|---|
| 手冊 53 題：檢索給到正解 | 52/53 | **50/53** | ↓2 |
| 手冊 53 題：引文命中（給到時） | 50/52 | 50/52 | — |
| 手冊 53 題：誤拒 ／ 幻覺引文 | 0 ／ 2 | 0 ／ 2 | — |
| 手冊 53 題：平均引用頁數 | 1.68 | 1.70 | — |
| 規則類 6 題：有作答 | 5/6 | **6/6** | ↑1 |
| 離題 12 題：正確拒答 | 11/12 | 11/12 | — |
| 引用到 `rules/*` 的次數 | 0 | 13 | |
| 平均秒數（非評選依據） | 47.1 | 29.7 | |

**② 的預言成立，而且是可以指名的兩題**：被擷取物擠出 `topK` 的是
「要抓出超過門檻天數都沒有進出的料」（呆滯庫存）與「發料的成本是用哪個時點的」。
**這個錨從此會動**，之九那種「這個值對每顆模型都相同、本來就不該動」的偵測法，
在擷取物語料上線後不再可用——要偵測前提被污染，得改看別的不變量。

**① 的預言也成立，但這一輪的「引到正解頁」整欄不可用。** 報表印的是 1/6，
看起來像擷取物讓引文變差；實際上是 `slug` 方案在這一輪跑的**同時**
從段落序號改成標題路徑雜湊（定案 #48），題庫的 `Expected` 還寫著 `rules/currency-03.html`，
模型引的是 `rules/currency-d151520a.html`。逐則核對內容後——
`d151520a`＝舊 `-03`、`9b1a9a6a`＝舊 `-06`、`855873ea`＝舊 `-18`——**六題全部引對，真實成績是 6/6**。
記在這裡是因為這正是 ① 描述的失敗形狀（答對被記成引錯頁），只是這次的成因不是題庫沒補，
而是**補了之後鍵又換了一次**。

**一個副產物：那兩題「退回段落探針」量不到閘門。** 無擷取物臂 0/2「正確拒答」，
但該拒未拒的原因是**手冊本來就答得出來**（「採購單存的總金額是交易幣還是本幣」
直接從 `pur/purchase-order.html` 答出來）。探針必須是手冊也沒有的東西，
否則量到的是誤報——這一條 `RejectedRuleProbes` 的類別註解自己寫過，
砍掉候選題時用的正是這個理由，但保留下來的兩題沒有再用同一把尺量一次。
**閘門本身沒問題**：13 次 `rules/*` 引用沒有一次落在未核可的段落上。

**沒有變的**：誤拒 0、幻覺引文 2、平均引用頁數、離題拒答 11/12（兩臂都把
「鸚鵡螺保養週期」當成保養類問題答了，那是字形假朋友，與擷取物無關）。
生成變快（47.1→29.7 秒）是因為擷取物塊比手冊塊短，prompt 跟著變小。

##### 錨會動之後的替代品：`ManualRetrievalAnchorTests`（2026-08-17）

之九靠「52/53 對每顆模型都相同、本來就不該動」抓到權限被別的進程改掉。
上面那張表證明**這個錨從此會動**，於是它再也分不出「語料變多」與「前提壞了」——
而它防的正是最難發現的一類錯誤：前提壞掉、數字照樣好看、沒有紅燈。

替代的不變量是**把擷取物排除在候選之外再算同一個值**：

| 量法 | 值 |
|---|---|
| 混合語料、無擷取物（上表第一臂） | 52/53 |
| 混合語料、有擷取物（上表第二臂） | **50/53**　← 會動 |
| **手冊-only 錨**（索引裡有 472 塊擷取物，但不參與候選） | **52/53**　← 不動 |

它不隨語料規模動（擷取物再多也不參與），不隨生成模型動（整條路徑不碰生成模型），
但**權限一被收窄就會掉**——權限過濾在 `DocChunkFilter` 的 SQL 裡，
手冊分支照樣要比對 `MenuKey`。28 秒跑完，便宜到可以常態跑。

**做法是「取深一點再濾」，不是加一個 SQL 參數**：`DocChunkFilter` 的參數編號
`{0}`–`{2}` 是與所有呼叫端的約定，多塞一個會把每個呼叫端的編號推移一位。
而且不必改——`semantic-first` 下名次是一個總序，**把擷取物從較深的候選裡濾掉
再取前 5 個手冊塊，等同於「只在手冊裡取前 5」**。
⚠️ **這個等價只在 `semantic-first` 成立**（定案 #27 的預設）：RRF 的名次由候選集合決定，
濾完再算 ≠ 濾過之後才融合。測試自己會先驗模式，不讓它在 RRF 下安靜地給出一個看起來正常的數字。

**這裡把首測值寫成常數，與定案 #29／#40 不衝突**：那兩條講的是**品質指標**
（要拿來比較模型好壞的東西），首測值當目標值會讓後續的改善無從判斷。
這一支不量品質，它量的是**環境還不還是原來那個環境**——對前提而言，
「和上次一樣」正是唯一有意義的斷言。它掉下來時依可能性排序是
① 權限被收窄沒還原 ② 嵌入模型被換掉 ③ 手冊真的改版，
**只有 ③ 該更新那個常數，而且要跟手冊的變更同一個 commit**——單獨改它等於把金絲雀關掉。

### 10.9 落地順序（S1 → S2 → S3）

| 步驟 | 內容 | 需要模型 | WSL 磁碟增量 |
|---|---|---|---|
| **S1** ✅ **2026-08-05 完成** | HTML 解析、雙語拆分、切塊、雙字切詞、`tsvector` 詞彙檢索、L1 權限過濾、評測題庫 | **不需要** | **0** |
| **S2** | 接 Ollama 嵌入 ＋ 對話，驗證管線與權限帶入 | `gemma3:1b`／`llama3.2:1b`（**不採 `qwen`，見 §四**） | ~1–2 GB（**Windows 端**，見 §17.11） |
| **S3** | 答案品質評測、裁決待決 #5 選型 | 8B Q4 | ~5 GB（**Windows 端**） |

**S1 是最容易做錯、卻完全不需要 LLM 的一段**（§17.11.3 的判斷：失敗模式都在 S1）。

#### S1 落地紀錄（2026-08-05）

模組 `Modules/QA/`，開關 `Features:QaAssistant:Enabled`（預設關閉）。

| 元件 | 內容 |
|---|---|
| `CjkTokenizer` | 重疊雙字切詞 ＋ `ToOrTsQuery`；索引期與查詢期**共用同一個方法** |
| `ManualDocParser`（AngleSharp 1.5.0） | 雙語拆分 → `h2` 切塊 → 表格分隔 → 截圖語系 fallback |
| `DocChunk` / `QaDbContext` | 獨立上下文 ＋ `__EFMigrationsHistory_Qa`，migration 落 `Modules/QA/Data/Migrations` |
| `DocIndexBuilder` | 掃描 ＋ SHA256 增量 ＋ MenuKey 映射（反查 `HelpDocResolver`） |
| `ManualSearchService` | 語系硬過濾 → 權限過濾（SQL 內）→ `to_tsquery` OR ＋ `ts_rank` |
| `DocIndexBackgroundService` | 啟動時增量重建（§7.1 已定案的 `BackgroundService` 路線）；例外一律吞掉並記錄——**.NET 8 的 `BackgroundServiceExceptionBehavior` 預設 `StopHost`，逸出例外會關掉整個 ERP** |

**實測**：134 頁 → **563 chunks**；純文字 418 KB。
檢索品質（30 題題庫，純詞彙通道）：**Recall@1 60%、@3 87%、@5 97%、@10 100%、MRR 0.748**。

**測試**：單元 56（切詞 31／解析 17／語料守門 8）＋ 整合 21。
所有守門均經**突變驗證**：切詞 3 個、解析 7 個、檢索 4 個突變，各自被對應的測試抓到。

#### 過程中揪出並修正的四件事

1. **`plainto_tsquery` 是 AND 語意** → 「銷售排名怎麼看」會要求文件同時含有語氣詞「怎麼」「麼看」，
   **每一則自然語言提問都回 0 筆**。改 `to_tsquery` 的 OR 析取式，精確度交給 `ts_rank`。
2. **開關在兩個啟動階段答案不同** → `RegisterServices` 讀 `builder.Configuration`、
   `Configure` 讀 `app.Configuration`，測試用 `ConfigureAppConfiguration` 追加來源時只有後者看得到，
   於是去解析一個從未註冊的 `QaDbContext`，**應用程式啟動即崩潰**。
   修法：模組把註冊當下的判定存成欄位，`Configure` 不重讀組態；兩階段不一致時出 warning。
3. **手冊原始碼缺陷**：`qcr/spc-analysis.html` 有一段漢字漏在 `lang` 標記之外
   （且 `<` 未跳脫），該句**今天就會出現在英文版頁面上**。已修，並加常設守門掃描全 134 頁。
4. **語系硬過濾原本測不出來**：純中文／純英文提問的詞彙本來就不重疊，拿掉過濾器照樣全綠。
   補上以拉丁代號（`SalesRanking`）提問的案例才逼得出滲漏——因為路由與代號在中英兩版是同一個詞。

#### 尚未做（S1 範圍外，刻意）

- **相關度門檻**：實測兩群分數重疊，純詞彙通道訂不出安全值，延到 S2（§10.8）。
- **對話畫面（S-7）與 API**：S1 只有索引與檢索服務，沒有使用者入口。
- **手動重建的管理動作**：刻意延到 S-7 一起做。它需要一個畫面 → 選單登錄 ＋ MenuKey ＋
  R13／R23 閘門 ＋ 雙語資源（§16.2），為一個按鈕付這整套代價不划算；
  而 S-7 本來就要走完那一輪。在那之前，重建的觸發方式是**重啟應用程式**
  （`DocIndexBackgroundService` 會做增量重建，內容沒變時 134 檔全略過、約 0.1 秒）。

同一份題庫之後直接作為 S3 的選型評測基準，**不是額外的工**。

> 原本這裡寫「S2 之前要切 `.wslconfig` 的 `networkingMode=mirrored`」。**該路線已放棄**——
> 它會 `wsl --shutdown`（兩個 distro 一起停），且把 RockyLinux 的 `*:3306`／`0.0.0.0:22`
> 綁上主機介面，而**本機沒有 Windows 管理員權限，補不了防火牆規則收斂**。
> 改走 SSH 反向通道，零提權、零 port 綁定變更（§17.11.2）。

#### S2 落地紀錄（2026-08-06，進行中）

| 元件 | 內容 | 狀態 |
|---|---|---|
| `OllamaClient`（`ILlmClient`／`IEmbeddingClient`／`IOllamaDiagnostics`） | 原生 `/api/chat`（`think:false`）＋ `/api/embed`；六道守門 | ✅ |
| `DocChunk.Embedding`／`EmbeddingModel`／`EmbeddingDim` ＋ migration | `real[]`，不用 pgvector（§10.6） | ✅ |
| `DocVectorStore` | 記憶體精確餘弦；模型／維度不符**拒絕檢索** | ✅ |
| `DocChunkFilter` | 語系＋權限的 SQL 片段，**兩個通道共用一份** | ✅ |
| `DocIndexBuilder.EmbedPendingAsync` | 批次補嵌入，與詞彙索引**分開失敗** | ✅ |
| RRF 融合 ＋ 塊層級評測 | 與 S1 基準對比，**結論與規劃假設相反**，見下 | ✅ S2-c |
| 相關度門檻重訂（§10.8） | 語意分數也訂不出安全門檻，**維持不設門檻** | ✅ S2-d |

**守門一律經突變驗證**：`OllamaClient` 的九個突變全部被對應測試抓到。
測試：QA 單元 109（＋53）、QA 整合 22。

**兩個刻意的失敗方向**（都寫在程式碼註解裡，此處只記結論）：

1. **嵌入失敗只降級、不中止。** 詞彙通道不需要任何模型，推論服務沒開時它必須照常運作；
   把嵌入失敗升級成整個索引失敗，等於讓一個**選用**相依決定**必要**功能的存亡。
   但降級一定要出聲，否則症狀只是「答得沒有以前準」。
2. **模型不一致則拒絕檢索，不「盡量用」。** 向量空間不可比沒有任何外顯訊號——
   照樣回得出 Top-K、分數也是合法浮點數，只是排序完全是雜訊。
   連「把不符的列濾掉、用剩下的算」都不允許，那正是「盡量用」。

#### S2-c 實測：**混合檢索的前提在這份語料上不成立**（2026-08-06）

同一份 30 題題庫、同一份索引、同一組權限，**塊層級**（不是 §10.7 那份頁面層級的前綴比較）：

| 通道 | Recall@1 | @3 | @5 | MRR |
|---|---|---|---|---|
| 純詞彙（S1 基準） | 18/30 (60%) | 26/30 | 29/30 | 0.748 |
| **純語意** | **30/30 (100%)** | 30/30 | 30/30 | **1.000** |
| RRF 融合（k=60，等權） | 27/30 (90%) | 30/30 | 30/30 | 0.944 |
| **semantic-first（現行預設）** | **30/30** | 30/30 | 30/30 | **1.000** |

**RRF 融合比純語意差。** 掉的三題全都是「詞彙通道把正解排在第 4／5／9 名」的情形
（`委外收貨的超耗扣款怎麼算` 4→2、`哪些客戶買最多` 9→2、`How to run MRP` 5→3），
等權 RRF 把那個錯誤名次帶進來，別的塊因此被推上第 1。

**而且沒有任何一題是詞彙勝過語意的。** 連 `NCR 處置方式有哪些`、`OEE 的公式`、
`StockShortage 這個畫面在做什麼` 這些**原本認為「專有名詞非詞彙通道不可」的案例**，
語意通道也全是第 1 名（詞彙分別是 1／1／5）。§10.5 當初把混合檢索的理由寫成
「專有名詞檢索」，這份資料**否定了那個前提**。

→ **定案 #27：預設改為 `semantic-first`**（`Features:QaAssistant:RetrievalMode`，
另一個值是 `rrf`）。語意名次為主，詞彙只在語意湊不滿 `topK` 時補位。

**但詞彙通道不刪**，三個理由：

1. 推論服務不可用時它是**唯一**的檢索方式（實測降級路徑已通，S1 的品質原樣保留）。
2. **30 題是很小的樣本，而且題目是自己出的**——足以否定「非有不可」，
   不足以證明「永遠不需要」。
3. 保留成本接近零；`rrf` 模式仍可一鍵切回並重跑同一份評測。

> 這裡的取捨句型值得記住：**實測到的代價（−3 個 Recall@1） > 假設中的好處（尚未觀察到）。**
> 反過來做（為了一個沒量到的好處付一個量得到的代價）就是規格裡最常見的那種錯誤。

#### S2-d 實測：語意分數**同樣**訂不出安全門檻，維持 §10.8 的決定

| 群體 | 語意分數（餘弦） |
|---|---|
| 30 題正解 | min **0.4094**、中位 0.6486、max 0.7393 |
| 4 題離題提問的最高分 | min 0.2665、中位 0.3961、max **0.4785** |

兩群仍然重疊。**但重疊的成因與詞彙通道不同**，這一點決定了後續怎麼處理：

| 離題提問 | 最高分 | 最像的段落 |
|---|---|---|
| 鸚鵡螺保養週期 | 0.2827 | 會計期間 |
| 今天午餐吃什麼 | 0.2665 | 生產準備看板 |
| 公司尾牙什麼時候辦 | 0.3961 | 月結作業 |
| **怎麼申請留職停薪** | **0.4785** | **計劃停機** |

撐高上界的那一題是**「停薪」被比成「停機」**——字形相近的假朋友，不是真的主題交集。
換言之這不是「離題提問其實與手冊有關」，而是嵌入模型在短提問上的失誤。

→ **維持 §10.8 的決定：不設硬門檻。** 依據是 §10.8.1 已實測的模型行為——
context 不含答案時，模型會照指示回「使用手冊中查無相關說明」（兩題各 10 tokens／0.7 秒）。
**與其用一個會砍掉正確答案的數字，不如讓模型說它不知道。**

> S3 可考慮的折衷（尚未實作）：把分數當**軟訊號**而非硬切點——
> 最高分低於某值時，答覆前綴改成「手冊中沒有直接說明，以下是最接近的段落」。
> 兩個方向都不會壞：分數偏低但其實有答案時使用者仍看得到內容，
> 分數偏高但其實離題時使用者已被提醒。

#### S2-e 實測：畫面用字「查詢／搜尋」不一致**不影響檢索**（2026-08-11）

另一個 session 回報了一個產品層的用字不一致，並推論它會打壞 Q&A 的詞彙通道。
用字不一致屬實，數字比回報的更尖銳——**同一個 resx 鍵有兩種中譯**：

| 事實 | 數字 |
|---|---|
| `data name="Search"` 的 zh-TW 值是「查詢」 | 72 個 resx |
| `data name="Search"` 的 zh-TW 值是「搜尋」 | 58 個 resx |
| 另一個鍵 `SearchLabel` 的 zh-TW 值是「搜尋」 | 20 個 resx |
| 同一個檔案內兩種譯法並存 | 12 個 resx |
| placeholder 也分裂：「請輸入搜尋條件後按『搜尋』」／「請設定查詢條件後按查詢」 | 26 ／ 15 |
| 英文兩者都是 `Search` | — |
| 手冊 138 頁中，內文（非截圖樣板字幕）真的寫「查詢」 | **116 頁** |

**但推論不成立。** 在 563 chunk 的實際索引上量**名次**（而不是絕對餘弦值——見定案 #40 的更正）：

| 提問 | top-1 | 分數 |
|---|---|---|
| 查詢按鈕在哪裡 | `index.html › 3. 畫面共用說明` | 0.3986 |
| **搜尋按鈕在哪裡** | **同一個 chunk** | 0.3575 |
| 要怎麼查詢資料 | `index.html › 6. 查詢與匯出` | 0.3855 |
| **要怎麼搜尋資料** | **同一個 chunk** | 0.3406 |

兩種問法的 **top-1 與 top-3 集合完全相同**，只是分數低 0.04–0.05。
`snowflake-arctic-embed2` 是多語模型，「搜尋」與「查詢」在它的空間裡本來就近。
**預設是 `semantic-first`（定案 #27），詞彙通道只在語意湊不滿 topK 時補位**，
所以「重疊雙字下『搜尋』與『查詢』零共同 bigram」這件事雖然為真，影響被架構擋住了。

→ **不因此改任何程式碼，也不加同義詞層。** resx 要不要統一是產品決定，與檢索無關。

> **唯一該記的殘留風險**：分數穩定低 0.04–0.05。目前不設硬門檻（S2-d），所以無害；
> 但 S2-d 末尾那個「軟訊號前綴」若哪天實作，**「搜尋」問法會比「查詢」問法更靠近切點**。
> 屆時要用的是同一句話的兩種問法去校準，不是只用手冊自己的用字。

> **方法上的一個自我修正**：第一版量測我直接對全部 563 chunk 排名，
> 結果 top-5 幾乎全是 `Lang=en` 的 `› Search` 段落，看起來像「中文提問撈回英文段落」的嚴重缺陷。
> 實際上生產路徑在 `WHERE` 就用 [`DocChunkFilter.LangAndPermission`](../../../Modules/QA/Data/DocChunkFilter.cs)
> 依 `DetectLang(question)` 過濾掉了——**那是我的量測漏掉過濾條件，不是系統的缺陷**。
> 這與 §10.8.2 之九「檢索被打壞卻只看到生成端數字」是同一類錯誤的鏡像：
> 這次是**量測比系統寬鬆**而報出假缺陷，上次是量測比系統窄而漏掉真缺陷。

### 10.10 L1 權限過濾的實作（順帶解掉既有缺口 G1）

已記錄的缺口 G1 是「L1 過濾每則提問要 N 次 DB 查詢」。實際上不必：

1. chunk 索引時就存好 `MenuKey`（頁路徑 → controller → MenuKey，反轉
   [`HelpDocResolver`](../../../Services/HelpDocResolver.cs) 既有的 `ControllerModule` 映射即可，零新增維護點）
2. 每則提問**一次**查出使用者的可讀 MenuKey 集合
3. 檢索時在記憶體以該集合過濾

→ 每則提問 1 次查詢，且與 §10.6 的記憶體向量檢索天然相容（同一輪 filter）。

---

### 10.11 擷取物語料：`.md` 與程式碼**不原文索引**（2026-08-06 新增）

起因是一個正確的觀察：**手冊回答不了所有問題。**
手冊 134 頁講的全是「這個畫面有哪些欄位、怎麼操作」，而
「GL 分錄怎麼產」「AVCO 怎麼算」「簽核什麼時候跳簽」只存在於程式碼與 `.claude/specifications/`。

擴充語料的方向是對的。**但這兩種來源都不可以原文進索引**，理由如下。

#### 10.11.1 為什麼不能原文索引

**理由一：規格 `.md` 裡有大量「已被推翻的舊方案」，而它們是用完整句子寫的。**
實測 `.claude/specifications/` 共 17,331 行：

| 反面敘述 | 處數 | | 開發者語彙 | 處數 |
|---|---|---|---|---|
| 待決 | 47 | | `migration` | 107 |
| 破口 | 13 | | `DbContext` | 105 |
| 初版 | 15 | | `MenuKey` | 73 |
| 原方案／不採用／更正 | 18 | | `IPlantEntity` | 37 |
| 已過時／已排除／已更正 | 5 | | `xmin` | 32 |

一段「原方案是把匯率存在明細行」在語意上跟正確答案**一樣像**——
模型沒有任何線索能分辨那句話前面掛著「不採用」。

> **本規格自己就是活例子**：§14.2 待決 #5 一路寫著「Embedding 模型已定案為 `bge-m3`」，
> 而該模型早在定案 #24 就因來源限制排除、定案 #28 已改為 `embeddinggemma`。
> **那句話如果進了索引，就是一個會被自信引用的錯誤答案。**（該行已於本次修訂更正。）

**程式碼沒有這個問題——程式碼只有現況，沒有「曾經考慮過但否決的方案」。
所以規格 `.md` 的歷史包袱反而讓它比程式碼更危險。**

**理由二：語彙不對。** 使用者問「為什麼我看不到別廠的單據」，
`.md` 裡的答案是「Global Query Filter 依 `PlantId` 過濾」——照原文引用等於沒回答。
擷取這一關同時做語彙轉換。

**理由三（僅程式碼）**：3.9 MB 的 `.cs`（已扣除 39.9 MB 的 migration 與測試）
整檔切塊出來的檢索單位很差，而且 `ManualDocParser` 綁死 HTML 結構（AngleSharp、`<h2>` 切點、
`<span lang>` 成對），對 `.md` 與 `.cs` 都不適用。

#### 10.11.2 三種來源，一種產物

| 來源 | 進索引的形式 | 理由 |
|---|---|---|
| 使用手冊 HTML（134 頁） | **原文** | 它本來就是寫給使用者看的 |
| 規格 `.md`（`.claude/specifications/`） | **擷取後改寫** | §10.11.1 理由一、二 |
| 程式碼 `.cs`／`.cshtml` | **擷取後改寫** | §10.11.1 理由二、三。✅ **2026-08-25 首次執行**（第一批 66 支／177 則待審），量測、裁決與實跑見 §10.11.13 |

**產物只有一種：面向使用者的規則說明。** 向量庫存的是這份說明，不是 `.cs`、也不是 `.md` 原文。
→ 連帶解掉三個問題：publish 產物沒有 `.cs`／`.md`（§10.11.6 另解）、切塊器不通用、
以及 §8.7「業務資料不進向量庫」——**規則不是資料，該條不受影響**。

#### 10.11.3 過期：綁來源 hash，stale **直接不給檢索**

擷取物最大的風險是**它會過期，而且不會出聲**：程式碼改了、說明沒重生，
Q&A 會用非常有自信的語氣講舊規則。**這比手冊過期嚴重——手冊過期至少有人會看到，
中間產物沒人會去看。**

→ 沿用 §10.3 已在運作的機制：擷取物存**來源檔的 SHA256**，來源一改即標記 stale，
而 **stale 直接排除於檢索之外，不做「先用著」**。
失敗方向與 §10.7 的 `DocIndexOutOfDateException` 一致：**寧可少答，不可錯答。**

##### 對帳：把「規格改一行」的代價從整份重審縮到那幾段（2026-08-24）

**保守的失敗方向是對的，代價卻被低估了。**2026-08-24 實測：472 段擷取物裡
**278 段是 stale**（`mfg-domain` 155／`mobile-pwa` 48／`business_data_flow_reconciliation` 75），
其中 **129 段是人審過的**。實際進得了檢索的只剩 136 段——**語料的 71% 被凍結，
而凍結的原因是三份來源檔各自改過幾行**。

而**不能靠重跑產生器解決**：`qa-extract-generate.py` 一律寫 `approved: false`
（定案 #45，且刻意沒有能產出 `true` 的參數），重跑一份等於把該份的人工審核歸零。

→ `scripts/qa-extract-reconcile.py`。**逐段對帳，沿用的條件是該段來源原文 byte 完全相同**
——文字一字未改，人核可過的就是這段文字，核可的語意沒有被繞過。

| 決定 | 理由 |
|---|---|
| **擷取當時的原文從 git 找回來** | manifest 記著 `sourceHash`，逐一比對該檔的歷史版本即可還原。三份全部還原成功。找不到就**明講退回整份重跑**，不假裝對帳過了 |
| **切段規則共用產生器的 `split_sections`** | 兩份實作會漂開，而漂開的症狀是「對帳說沒變、重跑出來卻不一樣」——比不對帳更糟 |
| **重跑走 `qa-extract-generate.py --sections` 子行程** | 不自己拼提示詞、不自己呼叫模型。那份契約只能有一份 |
| **任一段重跑失敗就整份不寫回** | 半套更新會讓沒重跑到的段落被 `sourceHash` 宣告成「照現在的原文寫的」，而它不是 |
| **段落改了卻沒產物（模型棄權／被閘門擋掉）＝移除該則** | 留著舊文字再更新 `sourceHash`，等於用新原文的名義背書舊文字 |
| **寫入逐段的 `sourceSectionHash`** | 下一次對帳就不必去 git 挖原文——那條路只在原文曾被 commit 時走得通。⚠️ **C# 這一側刻意不拿它算 stale**：要算就得在 C# 再實作一次 markdown 切段，而那正是上一列要避免的事。檔案層級的比對是保守的那一邊 |

**實測結果：重跑 10 段（3＋6＋1）解凍 129 段已審擷取物**，語料從 136 段回到 265 段。
被凍結的 129 段**沒有一段是真的受影響的**——全部落在「原文未變」。

###### 上面這套搬到 `.cs` 之後有兩個洞（2026-08-25 修）

對帳寫於 2026-08-24，那時只有 `.md`。第三個來源開通後（§10.11.13）它**從來沒有在
`.cs` 真的變動時跑過**——只驗過「不會壞」。補驗之後抓到兩個，兩個都是**跑起來完全正常、
結果是錯的**：

| # | 症狀 | 為什麼會這樣 | 後果 |
|---|---|---|---|
| ① | **改了程式碼、沒動註解 → 判「未變」** | `.cs` 的切段 body ＝ 註解＋宣告行（刻意的：只有註解值得送模型）。逐段比對比的就是 body，所以方法內部整段改寫在它眼裡是零差異 | 整支檔案的 SHA256 本來擋得住，逐段對帳把它放掉了。結果是**沿用已核可的說明、順手把 `sourceHash` 更新成現檔**——等於替一段可能已經不成立的行為重新蓋章，而檢索照常回答 |
| ② | **改一句註解 → 「刪掉一則、多出一則」** | 對帳拿 `headingPath` 當識別，但 `.cs` 的 `headingPath` 是**註解的第一句話**。產生器早就避開了這件事（`Section.key` ＝ 檔案＋宣告，slug 因此不隨措辭變動），對帳沒跟上 | 已審過的那則被當成刪除丟掉；`--apply` 還會拿舊標題去比對重跑結果，一律落空，把「重跑成功」誤報成「這幾段沒有產物」而中止 |

修法各一句話：**①`Section` 加 `fingerprint`**（這段註解「管到」的那段程式碼，＝從自己的
註解開頭到下一段註解開頭之前），staleness 比它、**永遠不送模型**，`.md` 留空即維持原行為；
**②識別改用 slug**，重放產生器的 `slug_of`（含碰撞加尾號那條路），slug 對不上才退回標題
——634 則裡有 3 則走這條，全都是兩段共用同一行宣告。`sourceSectionHash` 一併改成雜湊
fingerprint，與 `_classify` 同一把尺。

> ⚠️ ①的取捨要講清楚：fingerprint 讓「改了程式碼」重新進入待審，代價是**同一段附近
> 的任何程式碼改動都會讓該段重審**。這是刻意選保守的那一邊——擷取物講的是行為，
> 而 `.cs` 是唯一一種「來源檔改了、但擷取物讀的那幾行沒改」的來源。

**這三支腳本沒有任何測試，而以上兩個都不會報錯**，所以補了
[`scripts/qa-extract-selftest.py`](../../../scripts/qa-extract-selftest.py)：不呼叫模型、
不動工作樹，跑 manifest 一致性（slug 反推得到段落／`headingPath` 找得到段落／沒有 entry
落在標題重複的段落上）與對帳四情境（只改程式碼／只改註解／原封不動／整段刪掉）。
**兩個缺陷都逐一退回實測過會紅**，並接進 `pre-push-check.sh` 第 7 步。

> 📌 寫測試時踩到的坑值得記：情境①一開始把探針插在「註解後的第一行非註解行」，
> 而那行是空行——插進去之後**探針自己變成了宣告行**，body 跟著變，於是缺陷還在
> 也照樣綠。測試必須插在**宣告行的下一行**。「測試綠」與「測到了」是兩回事。

#### 10.11.4 核可：`Approved` 是唯一防線，不是流程裝飾

第二個風險是**擷取本身會幻覺**：讓 LLM 讀 `BuildJournalLines` 寫出分錄規則，
寫錯了沒有任何機制會發現，而它會被當成權威答案引用。

→ 擷取物**必須人工核可才進索引**（`Approved` 旗標），不得自動上線。
搭配 §10.11.5 的開放查詢後，**這一關升格為唯一防線**（原因見下節）。

##### 審核清單：`scripts/qa-extract-worklist.py`（2026-08-24）

核可的成本是逐則讀，而摩擦不在判斷、在**翻 JSON 去對照原文**。這支把
「要讀什麼、先讀哪一則、對照哪一段原文」擺在同一頁上，並沿用產生器的旗標判定
（語彙外洩、原文沒有的數字），再加兩條**審核者導向**的句型標記：

| 旗標 | 為什麼要標 |
|---|---|
| **含「可繞過管制」句型** | 定案 #37 的判定依據正是 `mfg-domain.md` §26 那三句「不做備料直接開工即可繞過 FPI」。這種句子讀起來完全像一條正常規則，機器判不出對錯，**但判得出句型** |
| **疑似非現況（時程／規劃／建議）** | 定案 #39：講過去與講未來都不是現況，而改寫最擅長把「預計會做」寫成「系統會」。⚠️ **實測必須是旗標不是排除**：`Phase A：產能模型（已實作）` 底下是現行規則，`Phase C：排程引擎` 是「目前處於開發階段……預計會透過」——標題長得一樣，只有讀內容分得出來 |

排序即建議：有旗標的最前面，其次是**該份已投入的審核量**（`mfg-domain` 已審 96 則，
續審的邊際價值最高）。`--approve <slug>` 只省掉手改 JSON 的編輯動作：
它一次只認明確指名的 slug、不接受萬用字元、拒絕在過期的 manifest 上動作，
核可仍是人的動作且記在 git（定案 #45）。

**2026-08-24 現況：未核可 192 則，其中 37 則帶旗標。**

#### 10.11.5 權限：開放查詢，語料按「操作 vs 規則」切分

**擷取物不設受眾閘門，所有使用者都能問。** 最重要的後果是一句話：

> **安全邊界從「執行期權限過濾」搬到「索引前的內容審核」。**

這兩道關的失敗方向差很多：**權限過濾漏了，會有人抱怨看不到東西；
內容審核漏了，不會有任何訊號**——沒有錯誤、沒有告警，就是有人某天問到了不該答的內容。

→ 因此收錄範圍不是取捨題而是必要工序：`.claude/reviews/`、
**§8.1「反向發現：既有暴露」**這類逐條列出現存權限破口的段落**絕對不可收錄**。
把弱點清單做成任何人問得到的功能，是本節唯一不可退讓的紅線。

**手冊維持 MenuKey 過濾，不跟著開放。** 否則會出現「使用者問不到 PUR 畫面的手冊、
卻問得到 PUR 的規則說明」——**這種不一致比全嚴或全鬆都糟**，因為沒人能預期系統答得出什麼。
解法是**按問題類型切，不按權限切**：

| 語料 | 回答的問題 | 權限 |
|---|---|---|
| 手冊（原文） | 「這個畫面**怎麼操作**」 | 綁畫面 → 維持 §10.4／§10.10 的 MenuKey 過濾 |
| 擷取物 | 「**規則是什麼、為什麼**」 | 不綁畫面 → 開放 |

配套：**擷取時不寫操作步驟，只寫規則。** 兩種語料不重疊，該不一致也就不會被使用者察覺。

實作面：`DocChunkFilter` 需要一個 corpus 分支——現行三條 MenuKey 分支對擷取物不適用。

> **一個不要誤記的點**：開放查詢**不會**讓向量快取變簡單多少。
> `DocVectorStore` 當初不做跨請求快取，寫的理由是**失效處理**（索引重建、換模型後答案還是舊的），
> 不是權限。開放只讓候選集對所有使用者一致，失效那個問題還在，仍要靠索引版號解。

#### 10.11.6 部署：**建置期產索引，隨 DB 出貨**

`DocIndexBuilder` 目前掃 `wwwroot/docs/user`，而 `wwwroot` 會隨 publish 走。
**但擷取物的來源檔不會。** 實跑 `dotnet publish -c Release` 清點產物（2026-08-06）：

| 產物中 | 數量 | 說明 |
|---|---|---|
| `.cs` | **0** | 全部編成 DLL |
| `.md` | **2** | 且都是 `wwwroot/lib/jquery-validation/LICENSE.md` 這類**第三方授權檔** |
| `wwwroot/docs/user/*.html` | **134** | 手冊全數隨 `wwwroot` 進產物 |

→ **規則不是「`.md` 不會被複製」，而是「只有 `wwwroot` 底下的東西會走」**
（[`SmartFactory.MES.csproj`](../../../SmartFactory.MES.csproj) 另只帶 Security dll／`appsettings`／`license.json`）。
`.claude/specifications/` 一份都沒有進產物。
照現行「執行期掃檔案」的寫法，**開發機好好的，正式機索引 0 筆而且不會報錯**。

三條路與取捨：

| 方案 | 問題 |
|---|---|
| **建置期產索引隨 DB 出貨** ✅ **採用** | 需要一個離線建置步驟；核可流程天然落在這裡 |
| 把來源放進 `wwwroot` | **等於公開原文**，與 §10.11.1、§10.11.5 直接衝突 |
| 只在開發環境啟用 | 正式機答不出來，等於沒做 |

#### 10.11.7 本節原本未決定的兩點（2026-08-11 定案）

兩點都已裁決，**不是量出來的，是從既有定案推導的**——記著這個出處，
日後若要翻案，該檢查的是「推導所依賴的定案是否仍成立」，不是重新憑感覺選一次。

**① 擷取由誰執行 → 離線批次產草稿 ＋ 人工逐段核可（定案 #43）。**
兩個端點都被既有定案排除：全自動等於把 §10.11.4 的 `Approved` 拿掉，
而定案 #31 已把它從建議升格為**唯一防線**；全人工在 17,331 行規格加全部程式碼的規模上做不完。
核可粒度是**段落**不是檔案（定案 #37）。
本點**不影響 §10.11.3 的 stale 機制**——無論誰寫，都要綁來源 hash。

**② 儲存位置 → 沿用 `DocChunk` 加 `corpus` 欄位（定案 #42）。**
兩張表等於兩處都得記得重嵌，而漏掉的那一半不會報錯，
只會安靜地用舊向量檢索——這正是 §10.7 表存 `EmbeddingModel`／`EmbeddingDim` 要防的那種失敗。

> 代價要先講清楚：權限過濾從此要依 `corpus` 分流——
> 手冊走 MenuKey、擷取物開放（定案 #31／#32）。
> **同一張表、兩套權限規則**，寫的時候很容易只記得其中一套。

#### 10.11.8 收錄清單審核（2026-08-06 完成，解除待決 #36）

**最重要的結論：檔案層級的白名單不成立。**

原本的假設是「挑出安全的檔案整份收，危險的整份不收」。逐份量測後這個假設被一個反例推翻：

> `mfg-domain.md`（3,346 行）**同時是全庫最有價值和最危險的一份**。
> 它是製造領域規則的唯一權威來源（FPI、備料、派工、成本、簡化模式全在裡面），
> 但 §26 有這樣三句話：
>
> > 「派工 `Scheduled` 時**不做備料直接開工**，`StartSession` 的狀態檢查為
> > `Scheduled or ReadyToStart or InProgress` → 通過，**FPI 完全被繞過**。」
> > 「**即使工序 `RequiresFpi = true`，只要不點備料就能開工。**」
>
> 這是一句可直接照做的**跳過首件檢驗**指引，與 §8.1「反向發現：既有暴露」同一類，
> 落在定案 #31 的紅線上。整份收＝把紅線內容一起收進去；整份不收＝製造規則全部答不出來。

因此**審核單位是段落，不是檔案**。而這正好是定案 #30（不原文索引）本來就會帶來的性質：
擷取是一次改寫，排除在改寫時自然發生——擷取「FPI 守門規則現況」時就是不要寫那句繞過方法。
真正被人工核可（定案 #31）的對象是**擷取物**，原文清單只決定「從哪裡擷取」。

**逐份判定**（`.claude/specifications/` 21 份 ＋ 根目錄，量測欄位為該檔命中行數）

| 檔案 | 行數 | 推翻 | 破口類 | 憑證類 | 判定 |
|---|---:|---:|---:|---:|---|
| `mfg-domain.md` | 3,346 | 17 | 4 | 1 | ✅ 擷取，**§26 缺口 A 段落強制排除** |
| `inventory.md` | 1,444 | 2 | 2 | 1 | ✅ 擷取（「繞過」兩處指 `IgnoreQueryFilters`，開發提醒，不擷取） |
| `ord-module.md` | 1,423 | 2 | 0 | 14 | ✅ 擷取（`Token` 全為防偽權杖，非憑證） |
| `business_data_flow_reconciliation.md` | 1,105 | 2 | 3 | 2 | ✅ 擷取（破口 3 處皆標 ✅ 已完成，屬歷史） |
| `APPROVAL-WORKFLOW.md`（根目錄） | 880 | 0 | 0 | 6 | ✅ 擷取——**風險輪廓最乾淨的一份**，§6 簽核操作說明／§7 組織異動是純業務 |
| `currency.md` | 356 | 3 | 0 | 1 | ✅ 擷取（匯率方向慣例正是手冊答不出的規則類問題） |
| `pur-domain.md` | 475 | 1 | 0 | 0 | ✅ 擷取 |
| `inventory-advanced.md` | 271 | 0 | 0 | 0 | ✅ 擷取 |
| `management-reports.md` | 215 | 0 | 0 | 0 | ✅ 擷取 |
| `qcr-domain.md` | 183 | 0 | 0 | 0 | ✅ 擷取 |
| `bas-master-data.md` | 172 | 0 | 0 | 0 | ✅ 擷取 |
| `mobile-pwa.md` | 1,279 | 1 | 0 | 29 | ⚠️ 部分——只取業務行為段落，離線權杖與設備指紋機制不取 |
| ~~`reconciliation-decisions.md`~~ | 140 | 1 | 0 | 0 | ❌ **2026-08-24 改判為不收**（原判「⚠️ 部分，3 處待決須排除」）。改判有兩層：① 那條段落級排除**從未被寫進 `SECTION_FORCE_EXCLUDE`**，於是整份照收（14 則、核可 0 則）；② 更根本的是**整份都是待決事項**——文件開場白自述「僅用於分析和提供建議，不會實際修改任何會計上的意義，需要決策的部分必須由業務或財務人員決定後才能實作」，這是定案 #39 的未來式那一類。而改寫會把這件事抹掉：實際產出的兩則寫成「系統只會將後續沖銷的材料成本計入，導致單位成本偏低」，讀起來就是現行行為的權威描述 |
| `roadmap-fin-cst-sub.md` | 859 | 3 | 1 | 11 | ❌ roadmap ＝未來式，**描述的不是現況** |
| `consistency-framework.md` | 794 | 4 | 7 | 1 | ❌ R/Cx 開發約束，終端使用者問不到 |
| `test-hardening-plan.md` | 1,015 | 4 | 8 | 4 | ❌ 開發流程 |
| `mobile-pwa-appendix-unused-spa.md` | 525 | 0 | 0 | 27 | ❌ 檔名自述 `unused`，已廢方案 |
| `STATUS.md` | 36 | 1 | 0 | 0 | ❌ 純進度 |
| **`llm-qa-and-report-tools.md`（本檔）** | 3,151 | 45 | 24 | 42 | 🚫 **紅線**——三項指標全庫最高，且是本功能自身的開發規格 |
| `report-permission-inventory.md` | 535 | 7 | 4 | 4 | 🚫 **紅線**——§8.1「反向發現」的本體 |
| `security-assessment-and-plan.md` | 158 | 1 | 6 | 20 | 🚫 **紅線**——158 行內 6 處破口、20 處憑證，密度全庫最高 |
| `security-m3-2fa-design.md` | 64 | 0 | 0 | 9 | 🚫 **紅線**——2FA 設計細節 |
| `CONSULTANT-ONBOARDING.md`（根目錄） | 719 | 0 | 0 | 66 | 🚫 **紅線**——含開發 DB 密碼字面值、授權核發與環境指紋取得程序 |
| `DEVELOPMENT.md`、`MOBILE-ARCHITECTURE.md`、`PRINT-ARCHITECTURE.md`、`CLAUDE.md`、`.claude/LOCAL_RULES.md` | — | — | — | — | ❌ 開發者文件，無終端使用者價值 |
| **`.claude/reviews/` 全目錄** | — | — | — | — | 🚫 **紅線**（定案 #31 已定） |

> 判定符號：✅ 擷取來源／⚠️ 段落級部分擷取／❌ 不收（無價值或非現況）／🚫 紅線（安全，不得收錄亦不得改寫後收錄）。

**量測方法**：`推翻` ＝ 命中「原方案／不採用／已更正／已過時／已排除／初版／放棄」的行數；
`破口類` ＝「破口／漏洞／未修／暴露／繞過／bypass／形同虛設」；
`憑證類` ＝「password／secret／token／金鑰／私鑰／指紋／license／CHANGE_ME」（不分大小寫）。
這三個計數是**篩選訊號不是判準**——`ord-module.md` 的 14 次 `Token` 全是防偽權杖，
而 `mfg-domain.md` 只有 3 行命中「形同虛設／完全被繞過」卻足以讓它進不了整份白名單。
**高分要查，低分不代表安全。**

**另一個獨立的失效模式：未來式。** `mfg-domain.md` 有 104 行命中「規劃中／後續／Phase X／下一階段」。
規格描述「將來要做的規則」，對規則類問答而言與過期同樣有害——差別只在一個講過去、一個講未來，
兩者都不是現況。故擷取時**只擷取已實作的現況**，`roadmap-fin-cst-sub.md` 整份排除即為此故。

#### 10.11.9 機制層落地（2026-08-13，S3）

**只做機制，不做內容。** 擷取物一列都還沒有——本次落地的是「有了以後，它會被怎麼擋」。
兩者刻意分開：閘門必須先於第一批內容存在，否則第一批內容進索引時是**沒有任何閘門**的狀態。

| 規格條文 | 落地物 |
|---|---|
| §10.11.7 定案 #42（同表加 `corpus`） | `DocCorpus.Manual`／`Extract` 常數；`DocChunk.Corpus` 欄位 |
| §10.11.3 過期 | `DocChunk.SourcePath`／`SourceHash`／`IsStale` |
| §10.11.4 核可 | `DocChunk.Approved` |
| §10.11.5 兩套權限 | `DocChunkFilter.LangAndPermission` 最外層改成 `Corpus` 二選一 |

migration `AddDocChunkCorpus` **不需要資料回填**：五個欄位都有資料庫端預設值，
既有 662 列手冊靠 `Corpus DEFAULT 'manual'` 自動落在正確的語料別上。

##### 三個寫的時候才看得出來的東西

**① `IsStale` 必須是物化欄位，不能查詢期比對。**
規格只寫了「來源一改即標記 stale」，沒寫誰來比對。查詢期比對 `SourceHash` 與現檔立刻撞上
§10.11.6 自己量過的事實：正式機根本沒有來源檔（`.cs` 0 份、`.claude/specifications/` 一份都沒有）。
那樣只有兩種結果——全部誤判為 stale（整個語料消失），或一律當作新鮮（閘門形同虛設）。
**故過期判定只發生在建置期，結果隨 DB 出貨**，與 §10.11.6 的部署決定是同一件事的兩面。

**② `DocIndexBuilder` 不收斂範圍的話，第一次手冊重建就會靜默清空整個擷取物語料。**
它的最後一段是「索引裡有、但磁碟上掃不到的檔案就刪」——而擷取物的來源檔
**本來就不在 `wwwroot/docs/user` 底下**，於是每一列都符合這個條件。
不是邊角情況，是必然發生，而且下一次重建不會把它們補回來（重建只讀手冊）。
同表的代價在 §10.11.7 只寫到「權限要分流」，這一條比那個更早爆。

**③ 兩個內容閘門是 `AND` 不是 `OR`。** `Approved` 防的是擷取寫錯，`IsStale` 防的是來源變了；
兩者是不同的錯，少任何一個都不夠。寫成任一即可等於「核可過就永遠有效」——
而那正是 §10.11.3 說「比手冊過期嚴重」的那個失效。

##### 測試守的是失敗方向，不是功能

新增 5 支整合測試（真 PostgreSQL）＋ 4 支字面守門。值得記的是這幾條驗的都是**不該發生的事**：
未核可的擷取物連 `allowAll` 都檢索不到、已核可但 stale 的一樣檢索不到、
手冊重建後擷取物還在。功能面（核可且新鮮就查得到）只有一條。
比例是刻意的——這個設計的失效全部是「安靜地多給」或「安靜地全沒了」，沒有一種會拋例外。

> **尚未落地（需要另一輪）**：離線擷取批次（定案 #43）、逐段核可介面、建置期產索引並隨 DB 出貨的
> 實際步驟。在那之前 `SourcePath`／`SourceHash`／`IsStale` 沒有寫入端——
> **這是預期狀態而非未完成的缺陷**：閘門先立，內容後進。
> （核可介面的形式已於 §10.11.10 裁決，寫入端已補上。）

#### 10.11.10 核可介面：**走版控，不做 MES 畫面**（2026-08-13 裁決，定案 #45）

定案 #43 只寫了「離線批次產草稿 ＋ 人工逐段核可」，沒寫核可**在哪裡按**。
兩案是「MES 裡開一個核可畫面」與「manifest 進版控、核可＝ merge」。
**選後者，而且不是偏好問題**——三件查證過的事各自就足以否決前者：

| # | 事實 | 對「做成畫面」的後果 |
|---|---|---|
| ① | `QaDbContext` **沒有任何攔截器**（該檔自己寫明無 `AuditInterceptor`；`Program.cs` 只對主 context 掛稽核） | 畫面上的核可**不留任何稽核紀錄**——而 §10.11.4 說 `Approved` 是唯一防線。唯一防線＋零稽核 |
| ② | §10.11.6 已定案索引在建置期產生並隨 DB 出貨 | 畫面改的是正式機當下的 DB，與出貨內容會漂開，事後無法回答「當時到底核可了什麼」 |
| ③ | 核可粒度是段落（定案 #37），語料 17,331 行規格＋程式碼 | 這是 code review 等級的工作量。diff、上次審到哪、退回重來——git 天生就有，CRUD 畫面三樣都沒有 |

還有一項不是否決理由但方向一致：§10.11.5 已把安全邊界前移到內容審核，
**做成畫面等於新增一個執行期高權限面**——有該權限者可讓任意內容對全體使用者可答。
走版控則沿用既有的 PR review，不新增任何執行期權限。

**唯一真實的反對意見**是：規則內容的正確性是領域知識，顧問與使用者比工程師更有能力判斷，
把核可鎖進 git 等於把最懂的人排除在外。**這個問題不用 CRUD 畫面解**——
拆成兩件事即可：產生器另外輸出人可讀的審核頁（靜態 HTML，每段附「擷取自哪一段原文」對照），
**判斷交給懂規則的人，提交留在版控**。

> 另外補一個**只讀**的 MES 畫面（目前生效的擷取物、來源、核可時間、是否 stale）。
> 它解決可見性與事後追查，且不開放任何寫入，故完全不動安全邊界。
> **只有這一支適合做成畫面。**

##### 落地物

| 物件 | 位置 | 職責 |
|---|---|---|
| `ExtractManifest`／`ExtractEntry` | `Modules/QA/Contracts/` | manifest 契約（`sourcePath`／`sourceHash`／`entries[]`） |
| manifest 本體 | `.claude/qa-extracts/*.json` | **一個來源檔一份**，進版控。核可＝`approved: true` 被 merge |
| `ExtractManifestImporter` | `Modules/QA/Services/` | 讀檔、驗格式、重算來源 SHA256、**逐份比對後只重寫變動的那幾份**（2026-08-26 改，原為無條件全量重寫） |
| 觸發點 | `DocIndexBackgroundService` | 排在補嵌入**之前**，新段落同一輪就拿到向量 |

引文頁碼是 `rules/<slug>.html`（`DocUrl` ＝ `/docs/user/rules/<slug>.html`）。
**形狀刻意與手冊頁碼一致**——§10.8.2 之十一的三輪實測顯示模型是在對齊一個具體字面而非讀規則，
換一種形狀等於在沒有量測的情況下動引文行為。

**`slug` ＝ `<來源檔>-<標題路徑 SHA256 前 8 碼>`，不是段落序號**（2026-08-17 改，定案 #48）。
序號版（`currency-04`）綁的是「第幾段」而不是內容：在來源規格前面插入一個標題，
之後每一段的 slug 全部往後位移一格，**而沒有任何一步會報錯**。
實測 `currency.md` 插一個 `##`：19 個 slug 有 **16 個指到別的標題**
（第 4 段從「三、換算公式」變成「二之一、新加的章節」，其後逐一遞補）。
後果是 `DECISIONS.md` 記的退回理由、`ManualQuestionBank.RuleGrounded` 的正解頁
一起錯位，而**唯一的症狀是答案內容不對**。

雜湊只取 8 碼（32 bit），一份最多 160 則、碰撞機率約 3e-6；但碰撞的代價是
**匯入器整批中止**（同份內 slug 必須唯一，`Validate` 一拋例外，所有規則類問題一起消失），
所以產生器仍要接住：同份標題路徑重複或雜湊撞號時，把出現次序摻進雜湊輸入重算並印警告
（那一則的 slug 會退回「跟位置有關」，必須看得見）。
代價是換來的——標題本身被改寫時 slug 會變（那時它本來就該重審），
且 slug 不再看得出前後順序，順序改看 `headingPath`。

##### 兩個方向相反、都會靜默清空語料的陷阱

這兩條合起來才是完整的規則，**只記一條會做出另一種災難**：

| 情境 | 正確行為 | 寫錯的後果 |
|---|---|---|
| manifest **目錄不存在** | 直接返回，**一列都不刪** | 正式機沒有 manifest（不進 publish 產物）。照常執行「沒提到的就刪」＝**每次啟動清空整批出貨的擷取物**，而下次啟動也補不回來 |
| manifest **目錄在、某份被刪** | 該份的段落跟著消失 | 不刪＝Q&A 繼續引用一份已經沒有人維護的規則說明 |

判準是一句話：**目錄在＝這台機器是索引的來源，manifest 沒提到的就是被移除的；
目錄不在＝這台機器只是消費者，不做任何推論。**

##### 過期判定：「找不到」也算過期

`EvaluateStaleAsync` 對三種情況都回 stale：內容 hash 不符、**來源檔不存在**、找不到 repo 根。
中間那項最容易被寫成「找不到就跳過檢查」——那等於讓**檔案被改名或刪掉變成悄悄放行**，
而那正是最需要擋下來的一種來源變動。

##### 格式檢查一律拋例外

`slug` 不合法、`lang` 非 `zh`／`en`、`text` 空白、同份 `slug` 重複、`sourceHash` 非 64 字十六進位——
全部整批中止，與 `DocIndexBuilder` 同一立場。**這幾項壞掉都不會有錯誤，只會安靜地少答或答錯**：
slug 不合法產生一個永遠對不上的引文頁碼；語系寫錯讓那一段對兩種語系的使用者都消失（§10.4 是硬過濾）；
slug 重複則兩段共用一個頁碼，使用者點進去只看得到其中一段。

##### 未核可的段落**照樣匯入**

它會佔一列、會被嵌入，但由 `DocChunkFilter` 擋在檢索外。丟棄反而是錯的：
審核者要看得到「這一段被產出來但我沒放行」，丟掉的話下一次產生器會再產一次同樣的東西。

#### 10.11.11 產生器與審核頁（2026-08-14，S3）

| 物件 | 位置 | 進版控 |
|---|---|---|
| 產生器 | [`scripts/qa-extract-generate.py`](../../../scripts/qa-extract-generate.py) | ✅ |
| manifest | `.claude/qa-extracts/<來源檔名>.json` | ✅ **核可＝把 `approved` 改成 true 再送 PR** |
| 審核頁 | `~/.cache/projectz-qa-extracts/<來源檔名>.html` | ❌ 每次重跑就重產 |

##### 來源閘門寫成程式碼，預設拒絕

§10.11.8 的判定表在腳本裡是一份 `REDLINE` 集合，命中直接 `exit`，**沒有覆寫參數**。
理由是紅線的失敗模式無聲：對 `security-assessment-and-plan.md` 手滑跑一次，
產出的草稿就躺在 `.claude/qa-extracts/` 等著被誰順手核可。
把「唯一防線」押在「記得哪些不能跑」上，是把防線換成記性。
❌ 不收的那一類另外列——兩者被擋下來時該怎麼辦不一樣。

##### 三件事是實跑換來的，不是設計出來的

**① 模型給對了形狀，但不印那兩個標籤。** 首跑三段全判「輸出不符格式」，
而內容其實是對的：有標題行、有說明段，就是沒有 `標題：`／`說明：` 這兩個字面。
**沒有再去調提示詞**——§10.8.2 之二與之十一已經量過同一件事兩次，
為對齊字面改提示詞三輪全部持平或退步。改成解析器接受無標籤形狀，
但設兩道防：標題 ≤24 字且不以句號結尾、說明 ≥40 字。

**② 長度過濾吃掉了全庫最該擷取的一條。** `--min-chars 200` 把 `currency.md` §三 換算公式
（96 字）整段丟掉，而那一段就是「本幣金額 ＝ 外幣金額 × Rate」。
→ 切段器**不做任何長度過濾**，過濾移到呼叫端，而且**被過濾掉的要印出來、要上審核頁**。
**短不等於不重要：規格裡最短的段落往往是最濃的那一條。**

**③ 提示詞給了棄權選項，模型幾乎不用。** 19 段只棄權 1 段，於是 `ICurrencyService`
的介面定義被改寫成「系統會自動取得當前使用的本幣代號」，API 回傳結構被寫成
「回傳一個表示已找到但狀態為未成功的情況」。**這種空話不會被任何旗標抓到**——
沒有加料、沒有語彙外洩、讀起來也通順——但它會佔一列、會被嵌入，
然後跟真正的規則搶 `topK` 的名額（正是之十二 ② 那個問題的來源）。
→ 加一道確定性閘門：**圍籬內的程式訊號密度**。

> **用密度，不用圍籬佔比。** §三 換算公式整段都在圍籬裡（佔比 0.74），
> 但那是公式不是程式，訊號密度 **0.000**；同一份裡 §五、六、七的 C#／cshtml 範例是
> **0.028–0.093**，中間隔了一個數量級。用佔比當閘門會把那條規則跟程式範例一起丟掉。
> 門檻取 0.02，理由就是這個數量級的缺口，不是調出來的。

##### 首跑實測（`currency.md`、`gemma4:latest`、19 段）

| | 閘門前 | 閘門後 |
|---|---:|---:|
| 送進模型 | 19 段 | **8 段** |
| 產出擷取物 | 17 則 | **8 則** |
| 模型棄權 | 1 | 0 |
| 程式碼段擋掉 | — | **11** |

每段約 9–21 秒（提示詞短、生成短，遠快於 §10.8.2 每題 52.8 秒），整份約 2 分鐘。
**審核仍然是瓶頸，不是生成。**

> 自動旗標兩項（疑似語彙未轉換、原文沒有的數字）在這一份**零命中**——
> 而人工讀第一輪產物就抓到 `currency-02` 把換算方向寫反了。
> **旗標抓得到的和該抓的不是同一批**，這正是審核頁要並排原文的理由。

##### 守門測試：版控裡的 manifest 必須通得過格式檢查

核可＝人手改 JSON 再送 PR，所以**手改是正常作業不是意外**。改壞一個逗號的後果不是
「這份不生效」，而是整批中止（`Validate` 一律拋例外），症狀是正式機啟動時
規則類問題一起答不出來、手冊照常——完全不像匯入壞了。

**刻意不驗 stale。** 來源改了而 manifest 沒重生，設計上由執行期處理
（stale 直接排除於檢索之外，§10.11.3），那是「寧可少答」的安全方向。
在 CI 加硬閘門會把安全的降級變成建置失敗，而且會逼所有改規格的人先在本機跑一次 LLM，
連改錯字都要。**過期的可見性歸只讀畫面管，不歸 CI 管。**

##### 連帶修掉一個：受測應用程式不該吃開發者的工作樹

`currency.json` 一進版控，整合測試就開始出現被吞掉的
`23505 IX_DocChunks_FilePath_Lang_ChunkIndex`——兩個 factory 幾乎同時啟動，
各自的背景服務對同一個共用測試庫「先刪再插」同一批列。**套件仍然全綠**
（背景服務把例外吃掉了，那是它該做的），所以症狀只是日誌裡一條沒人看的 ERR，
外加擷取物列數會隨當下簽出什麼而浮動。
→ `TestWebApplicationFactory` 把 `ExtractManifestDir` 指到不存在的路徑，
讓背景服務走「目錄不存在→什麼都不做」。正式機本來就是這條路（§10.11.6）。

##### 只讀畫面：`/QaExtract`（2026-08-15 實作）

`Modules/QA/Controllers/QaExtractController.cs` ＋ `Modules/QA/Views/QaExtract/Index.cshtml`。
掛在系統管理群組下，因此沿用 `ADMINMOD` 矩陣（**只有 ADMIN 與 MGR 讀得到**，
`RolePermissionSeeder` 不必為它特判）。`Features:QaAssistant:Enabled=false` 時**連選單都不出現**。

| 決定 | 為什麼 |
|---|---|
| **列出全部，不只列生效中** | 標題那句「目前生效的擷取物」少講了一半：§10.11.3 的過期是**靜默排除**，只列生效中的話，症狀仍舊是「助理突然不知道這條規則了」而畫面上什麼都看不出來。生效／未核可／已過期三個總數放在查詢列上方，分頁後的清單回答不了「有東西被排除了」 |
| **未核可的段落只列存在，不顯示內文** | §10.11.5 把安全邊界整個押在人工核可上。畫面若印出未審內文，等於在閘門旁邊另開一扇門，而且是對全體有本畫面權限的人開的。要讀內文請看產生器的審核頁或 manifest——那兩者都在版控與本機，不在執行期 |
| **關鍵字只比對標題與頁碼** | 比對內文的話，未核可段落的文字能靠「查得到／查不到」被一段一段試探出來，而畫面從頭到尾沒印出過它 |
| **時間欄叫「匯入索引時間」不叫「核可時間」** | 核可＝manifest 被 merge（定案 #45），那個時間點**只存在於 git**，DB 沒有任何欄位承載它。照實命名，免得有人把它讀成「誰在什麼時候放行了這一段」。⚠️ **這是本畫面對原始需求（來源／核可時間／stale）唯一沒有滿足的一項**，要補得改 manifest 契約，不是改畫面 |
| **`QaDbContext` 走 `RequestServices` 取，不用建構式注入** | 關燈部署根本沒註冊它，建構式注入會讓手打網址變成「解析不到服務」的 500；正確答覆是 404 |

**兩件實跑才看得到的事：**

**① 安全斷言差點是假綠。** `DoesNotContain(未核可內文)` 這條斷言在還原 HTML 實體之前**永遠會通過**——
Razor 預設的 `HtmlEncoder` 把模型帶出來的中文編成 `&#x63DB;` 這種數值實體
（`IViewLocalizer` 的輸出不會，所以頁面標題是好的、只有資料是實體），
原始 HTML 裡本來就不可能出現那串中文字面，**即使畫面真的把未審內文印出來了**。
→ `QaExtractScreenTests.GetAsync` 一律先 `WebUtility.HtmlDecode`。
**這種假綠比沒有測試更糟**：它守的正是唯一那條邊界，而它會一直是綠的。

**② R5b 要加例外，不是加 resx 鍵。** `ExtractChunkVm` 的 `Approved`／`IsStale`／`IndexedAt`
是值型別屬性，R5b 要求它們在 SharedResource 有鍵。但這是唯讀清單的投影，
**沒有任何表單繫結它**，欄位標題全部來自該檢視自己的 resx——
補三個沒有畫面在用的鍵正是該規則註解裡說的「補 resx 鍵等於造假欄位名」。
→ 併入 `DocChunk`／`ExtractManifest` 那條既有的同類例外，不進 `baseline-R5b.txt`（那是給既有程式的）。

##### 兩個已知落差

**① 只產 `zh`。** §10.4 的語系是硬過濾，所以英文使用者問不到任何規則類問題。
不順手一起產的理由：核可粒度是段落，**英譯是另一段要核可的內容**，
混進同一輪會讓審核者在讀不懂的語言上按放行。

**② 引文頁碼 `rules/<slug>.html` 沒有對應的頁面可點（待決 #47）。**
手冊的引文點得下去（`wwwroot/docs/user/…` 是真檔案），擷取物的點不下去。
形狀維持一致是刻意的（§10.11.10），但「點了沒東西」要在 S-7 之前決定怎麼收。
→ ✅ **2026-08-14 已裁決，見 §10.11.12（定案 #47）：走 ①，就地展開。落差 ② 就此關閉，① 仍開著。**

#### 10.11.12 引文呈現：`rules/*` 就地展開，**不產靜態頁**（2026-08-14 裁決，定案 #47）

三條路裡選 ①（S-7 對話畫面把擷取物內容就地展開）。**先講一件三條路都成立的事，免得被當成選 ① 的理由**：
送進模型的 prompt 一個 byte 都不會變——頁面代號由 `QaPromptBuilder.PageCode(h.DocUrl)`
從 `DocUrl` 算出來，三條路都不動 `DocUrl`。所以 §10.8.2 之十一那三輪實測**不會因為本裁決失效**，
但它也**分不出**這三條路的優劣。真正分得出來的是下面兩件查證過的事。

##### 否決 ②（建置期產 `wwwroot/docs/user/rules/*.html`）

| # | 查證到的事實 | 對 ② 的後果 |
|---|---|---|
| ① | `app.UseStaticFiles()` 在 `Program.cs` **第 340 行**，`app.UseAuthentication()` 在 **第 415 行** | **`wwwroot` 底下的東西是匿名可讀的**，不需登入。手冊本來就寫給使用者看，那是知情的；擷取物是從 `.claude/specifications/` 與程式碼改寫出來的**內部級**內容，② 等於把它推上匿名 HTTP |
| ② | §10.11.6 已定案「索引在建置期產生、隨 DB 出貨」，權威在 DB | ② 會生出**第二份拷貝**。而 §10.11.3 的 stale 處置是「排除於檢索之外」——DB 那份被排除了，`wwwroot` 那份**還在照常提供**。變成「答案裡不再引用，但網址仍讀得到」，正好是 stale 處置想避免的反面 |

> 原本記在待決 #47 裡的「② 與 §10.11.6 不衝突（出去的是已核可的產物，不是原文）」**這句話本身沒錯**，
> 但它只回答了「原文會不會外流」，沒回答「產物該不該匿名可讀」。**是我當時把問題問小了。**

##### 否決 ③（引文不可點）

`QaPromptBuilder` 的類別註解把引文行的存在理由寫成兩條，第一條是
「**使用者要能點回手冊查證**——8B 模型會把兩頁的內容揉成一段話，沒有引文連結時使用者無從分辨哪一句有依據」。
而規則類問題**正是使用者最沒有別的地方可查證的一類**（手冊本來就答不出來，那是擷取物存在的理由）。
③ 等於在最需要查證的那一類上把查證拿掉。附帶還會讓引文的可點性變成「看運氣」——
同一則答案裡手冊引文點得下去、規則引文點不下去，而使用者無從預期。

##### ① 要做什麼（S-7 的實作契約）

已經有的東西就夠，**不需要新端點、新檔案、新權限面**：

| 需要的 | 已經在哪裡 |
|---|---|
| 這則答案的模型到底看到哪幾塊 | `QaPromptBuilder.SelectWithinBudget` **是 public 的，本來就是為了這件事**（見其註解） |
| 該塊的全文與麵包屑 | `DocSearchHit.Text` ／ `HeadingPath` |
| 頁面代號 → 塊的對應 | 一對一：`ExtractManifestImporter.BuildRows` 是 `entries.Select((e, i) => …)`，**一則 entry 一個 chunk，一個 slug 一塊** |

呈現規則三條：

1. `DocUrl` 前綴是 `/docs/user/rules/` → **就地展開**（可摺疊區塊，標題用 `HeadingPath`，內容用 `Text`），不畫 `<a href>`。
2. 其餘（手冊，`/docs/user/<module>/…`）→ **維持真連結**，行為完全不變。
3. 引了 `given` 以外的代號（幻覺引文）→ **純文字，不展開也不連結**。
   展開一個空白區塊會把「模型編了一個頁碼」偽裝成「這頁剛好沒內容」。

> **刻意不動幻覺判定。** 它比的是 `given`——真的進了 prompt 的代號——不是磁碟上有沒有那個檔
> （`ManualGenerationBenchmarkTests` 第 179 行）。之十二已經查證過這一點不會壞，
> 那裡寫「記下來是為了擋掉『順手修一下』」，本裁決就是那個會讓人手癢的時機。

✅ **2026-08-15 已實作**：`QaCitationPresenter`（純函式，8 個單元測試）＋ S-7 畫面。
落地時多出來的五個決定見 **§16.3.1**，其中兩個是實作才浮出來的：
`QaCitationKind` 若不帶 `[JsonConverter]` 會序列化成數字而讓**每一則引文都被標成幻覺**；
分類比對必須序數且區分大小寫，否則畫面與評測對「幻覺」的認定會分家。

#### 10.11.13 第三個來源（程式碼）的缺口量測（2026-08-25）

§10.11.2 那張表列了三個來源，**實際只跑過兩個**：使用手冊 HTML（原文）與規格 `.md`
（擷取物 457 段、核可 291）。**程式碼那一列從 2026-08-06 寫下至今從未執行過。**
本節是「要不要開它」的量測依據——**只量缺口，不產語料**，一則擷取物都沒生。

##### 先量成因，不是先量缺口

起因是使用者指出的因果：程式依規格寫，但**測試與修改後不一定回寫規格，且程式的粒度比規格細**。
這句話可以直接量：2026-05-01 以來動到產品碼 `.cs` 的 commit（`--no-merges`，
排除 `Tests/` 與 `/Migrations/`）共 **154 個，其中 99 個（64%）在同一個 commit 裡
沒有動任何 `.claude/specifications/*.md`**。

抽樣驗證同一件事的另一面（粒度）——關鍵字在規格與產品碼的檔數落差：

| 規則 | 規格 `.md` | 產品碼 `.cs` |
|---|---|---|
| `ProductUomConversion`（品項級單位換算） | **0** | 9 |
| `OffcutRate`／「下角率」 | **0**（中英兩種寫法都查過） | 31 |
| 「待驗倉」 | 2 | 11 |

> ⚠️ 「下角率」在整個 `.claude/specifications/` 只出現在**本檔**（§10.11.8 的收錄清單裡），
> 也就是說 BOM 規格從來沒寫過它，但程式有 31 個檔在算。**這正是缺口的形狀**。

##### 方法：不設門檻，改分層抽樣後外推

三支腳本在 `~/.cache/qa-code-gap/`（`harvest.py`／`measure.py`／`report.py`）。
**刻意不進版控**：它們是一次性的量尺，不是產物；真要開路時該改的是
`scripts/qa-extract-generate.py`，不是把量尺留在 repo 裡讓人以為那是管線。

| 步驟 | 做什麼 |
|---|---|
| `harvest.py` | 從 `.cs` 切註解單位：`doc` ＝連續 `///` 區塊＋其後宣告行；`inline` ＝連續 `//` 區塊 |
| `measure.py` | 每則候選加 `query: ` 前綴後嵌入（`snowflake-arctic-embed2`，前綴契約見 §10.7），對現行語料取**最高餘弦** |
| `report.py` | 分佈 ＋ 每帶抽 8 則，產左右並排審核頁（左＝程式碼寫了什麼，右＝語料最像的一塊） |

**不訂相關度門檻是刻意的**：§10.8 已經實測過那個門檻訂不出來，這裡再訂一個只會把
不確定性藏進一個看起來很精確的數字。改成「最高餘弦分佈 ＋ 分層抽樣人工判讀 ＋ 依帶寬外推」，
代價是結論帶區間，好處是那個區間是誠實的。

語料基準 ＝ **485 塊**（手冊中文 194 ＋ **核可**擷取物 291）。
**未核可的 166 塊不計入「已覆蓋」**——`Approved` 是唯一防線（定案 #31），
沒核可的段落根本檢索不到，拿它充當覆蓋等於自己騙自己。
英文側 492 塊不在基準內（語系硬過濾，§10.4），對本次量測無影響，但真要開路時是另一筆帳。

##### 規模

679 支產品碼 `.cs`／94,727 行 → 5,266 個註解單位 → 篩到 **1,369 則候選**（102,001 個中文字）：

| 篩掉 | 則數 | 理由 |
|---|---|---|
| 無中文 | 528 | 英文樣板註解，不是規則敘述 |
| 中文少於 30 字 | 3,255 | 「取得或設定…」這類無資訊量的樣板 |
| 紅線檔 | 53 | License／Fingerprint／2FA／Crypto／Auth（見下方硬前置②） |

留下 `doc` 764 ／ `inline` 605。再扣掉 **QA 模組自身的 298 則**（那是本規格的元敘述，
拿它當語料是循環引用）→ **1,071 則進入判讀**。

##### 分佈：低分不等於缺口（本節最該記住的一件事）

| 最高餘弦 | 則數 | 佔比 |
|---|---|---|
| < 0.40 幾乎無關 | 87 | 6.4% |
| 0.40–0.50 沾邊 | 359 | 26.2% |
| 0.50–0.60 部分相關 | 411 | 30.0% |
| 0.60–0.70 高度相關 | 344 | 25.1% |
| ≥ 0.70 極可能已覆蓋 | 168 | 12.3% |

中位數 **0.557**（全距 0.310–0.875）。

**直覺會說「分數最低的就是最大的缺口」，抽樣把這件事推翻了**：`< 0.40` 那一帶
8 則抽樣 8 則都是**實作細節**（QRCoder 產的 SVG 缺 `viewBox`、零向量餘弦會是 NaN、
`real[]` 與 pgvector 的取捨）——分數低不是因為語料答不出來，是因為**那根本不是業務語言**，
使用者也不會問。真缺口集中在 **0.45–0.65**：夠像業務規則所以嵌得進去，又不夠像所以沒被覆蓋。

##### 抽樣判讀（40 則）與外推

| 判定 | 則數 |
|---|---|
| 非語料材料（實作細節、內部取捨） | 15 |
| **真缺口**（使用者會問、語料答不出來） | **12** |
| 已被手冊或擷取物覆蓋 | 13 |

依帶寬外推 → **真缺口約 350–550 則**（嚴格只算「使用者會問的業務規則」約 190；
放寬到「任何規則性敘述」約 557）。**這個區間的寬度就是抽樣誤差本身**：
每帶只抽 8 則。要收窄得再抽 100 則——**在裁決開不開路之前不值得花這個成本**。

具名例證（四則，皆為實查的 `檔案:行號`）：

| 缺口 | 在程式碼哪裡 | 規格為什麼答不出來 |
|---|---|---|
| 換算查不到且 from ≠ to **拋例外而非回 1.0** | `Modules/BAS/Services/UomService.cs:83` ＋ 該處 12 行註解 | 規格 0 檔提到 `ProductUomConversion`。原本回 1.0 的後果是靜默的：收 10 BOX 記成 10 PC |
| 委外報價生效區間倒置 | `Modules/MFG/Models/OutsourceQuotation.cs:72-80` | 存檔正常、列表看得到，但查價**永遠落空**——使用者看到的是「報價不見了」 |
| 班制未指定時的 fallback 順序 | MFG 排程（工作中心預設 → 本廠預設） | 規格只寫「依班制計算」，沒寫查不到時往哪找 |
| AR 開票自動日記帳的借貸口徑 | FIN 開票 | 先前完全不入帳，導致應收出現負餘額 |

##### 哪個模組最該先做（0.45–0.65 缺口帶，扣除 QA 自身後合計 634 則）

| 模組 | 缺口帶／總數 | 比例 |
|---|---|---|
| FIN | 21／26 | 81% |
| 權限／資安 | 24／31 | 77% |
| BAS | 65／85 | 76% |
| RPS | 79／111 | 71% |
| ORD | 27／39 | 69% |
| 簽核 | 22／32 | 69% |
| ACT | 23／37 | 62% |
| INV | 56／102 | 55% |
| PUR | 36／67 | 54% |
| Core 其他 | 74／139 | 53% |
| MFG | 162／317 | 51% |
| QCR | 15／33 | 45% |

> ⚠️ 分桶要看檔名不能只看目錄：簽核、單號、權限這些 **Core 主體功能不在 `Modules/` 底下**
> （`Services/ApprovalService.cs` 等）。第一版依目錄分桶時「簽核 0 則」「Mobile 0 則」
> 是假零，不是真的沒缺口。

**建議起手範圍：BAS ＋ FIN ＋ INV 的「例外與 fallback 規則」，約 142 則。**
選這一組的理由不是它們比例最高，是**一輪審得完**——`.md` 那次是 457 段一次上桌，
審核節奏因此拖成三週（§10.11.8）。同樣的錯不必犯第二次。

##### 開工前的兩項硬前置（不是實作細節，是會靜默壞掉的）

1. **`split_sections` 對 `.cs` 回 0 段。** 它依 markdown 標題切段
   （`qa-extract-generate.py:244`），`.cs` 沒有 `#` 標題 → **不會報錯，只會產出空 manifest**。
   切段單位要改成「帶 XML doc 的宣告」。
2. **紅線必須從「檔名 regex」改成白名單。** 本次量測用 regex 擋掉 53 則是**權宜**——
   `.md` 側的閘門是逐檔列舉（`REDLINE` 集合 ＋ `ALLOW` 清單，預設拒絕，§10.11.11），
   而 regex 是預設放行。679 支 `.cs` 用 regex 守，**漏掉哪一支不會有任何跡象**，
   而漏掉的可能就是授權或 2FA。

##### 已執行（2026-08-25，使用者裁決「做」）

**第一批 66 支 `.cs` 跑完，產出 177 則待審擷取物**（BAS 74／INV 79／FIN 24）。
manifest 全部 `approved: false` 進版控——核可仍然是人的動作（定案 #45），這一批只是把東西擺上桌。

##### 第二批（2026-08-25）：PUR＋ORD＋簽核＋權限，42 支／140 則

選法與第一批相同（0.45–0.65 缺口帶所在的檔案），提示詞**一字未改**——第一批退回率
6／177（3.4%），沒有調它的理由。逐檔清單與逐則統計見
[`CODE-INVENTORY.md`](../../qa-extracts/CODE-INVENTORY.md)。

| 桶 | 檔案 | 缺口帶單位 | 擷取物 |
|---|---|---|---|
| PUR | 21 | 36 | 51 |
| ORD | 11 | 27 | 37 |
| 簽核 | 7 | 22 | 26 |
| 權限 | 3 | 24 | 26 |

「簽核」「權限」兩桶的檔案不在 `Modules/` 底下，slug 走 `code-core-*`
——本節上面那句「依目錄分桶會得到假零」，這一批是它的實例。

**送審前先去重，退掉 5 則 → 實際待審 135 則**（ORD 37 → 32）。三組逐字重複全部落在
ORD，且都是同一段註解被複製貼上到二～三支控制器。留哪一支沿用第一批準則
（來源檔最不常動的那支），但**第三組是兩則一起退**：擷取物的標題寫「系統資料變更需
**分批**處理」而本文寫「必須放在**同一個交易範圍內**完成」，**標題與本文互相矛盾**
——標題是模型自己下的，它把原文的「顯式交易」讀成了「分批」，而檢索是拿標題與本文
一起嵌入的，一則自相矛盾的擷取物**兩邊都污染**。

更根本的是那一段本來就是實作取捨（「`VoidDocAsync` 內部自己 `SaveChanges`，
所以外面要再包一層交易」），**第 7 條該擋沒擋住**。漏法與第一批
`OutsourceMaterialController` 那則同源：第 7 條擋得掉整段在辯論技術方案的，
擋不掉**「一句陳述句 ＋ 一個技術理由」**——句子本身讀起來就像一條規則。
**兩批各出現一次，這是已知的固定漏洞，不是偶發。** 逐則決定見
[`DECISIONS.md`](../../qa-extracts/DECISIONS.md)〈程式碼來源第二批〉。

###### 判過期的雜湊有三份，其中兩份不去 BOM（2026-08-25 修）

**這是第二批才露出來的**，因為第一批的 66 支來源檔一支帶 BOM 的都沒有。

`sha256_of_source` 的檔頭本來就寫著「必須與 `ExtractManifestImporter.Sha256`
逐位元組相同」並據此去掉 UTF-8 BOM。問題是**另外兩個呼叫端沒有走它**：

| 呼叫端 | 原本寫法 | 來源檔有 BOM 時 |
|---|---|---|
| `qa-extract-worklist.py:177`／`:360` | `sha256(src.read_bytes())` | 判為過期 → **靜默跳過不列入待審**；`--approve` 一併拒收 |
| `qa-extract-reconcile.py:68`／`:113` | `sha(blob)`／`sha(read_bytes())` | git 歷史找不到相符雜湊 → `old_text=None` → **每一則都判「已變」** |

第二批 3 支帶 BOM（`ORD/GoodsShipmentController.cs`、`ORD/SalesReturnController.cs`、
`PUR/PurchaseService.cs`），140 則裡有 **22 則**一產出就被排除在審核清單外。
**兩邊的症狀都是安靜地少收**——而少收在畫面上長得跟「這支沒東西」一模一樣。

修法：把去 BOM 的雜湊收成 `sha256_of_source_bytes` 一份定義，另兩支改成呼叫它
（`sha256_of_source(path)` 改為委派）。`sourceSectionHash` 那兩處雜湊的是段落文字、
不涉及 BOM，維持原樣。自我測試補一組並**退回實測會紅 2 項**。

> ⚠️ 該組有一項（「帶 BOM 的 manifest 其 `sourceHash` 是去 BOM 版本」）
> **退回修正照樣綠**——它比的是產生器自己的函式，測不到另外兩支。
> 標籤已照實改名。這與 §10.11.3 那個「探針插在空行」是同一類的教訓：
> **測試綠與測到了是兩回事。**

###### 兩道重複掃描不能互相取代

`duplicate_flags` 比的是**擷取物**，只在跑完模型之後才看得見；新增的
`qa-extract-worklist.py --source-duplicates` 比的是**原文**，開跑之前就看得見。

開跑前用來源層掃描預測「`ORD/ReturnRateTool.cs` 會產出一則與已核可的
`code-inv-stockshortagetool-583ce060` 逐字相同的擷取物」，**結果沒有發生**——
模型對同一段原文換了措辭。**來源層相同不蘊含擷取物層相同**，反之亦然
（原文不同、改寫撞成同一句，只有 `duplicate_flags` 看得見）。

> 🚨 **不要拿 `~/.cache/qa-code-gap/scored.jsonl` 掃重複**：`harvest.py:79` 在全域
> 用註解雜湊去重、只留第一次出現，於是拿它掃重複**恆為零**——而那個零跟
> 「真的沒有重複」長得一模一樣。實測踩過。切段一律走 `split_sections`。
>
> 附帶影響：本節那張模組缺口表也是從去重後的資料算的，**複製貼上的註解只算了一次**，
> 因此各桶的缺口帶單位數是低估。它只用來排審核順序，不是閘門，故不重算。

###### 退回名單只認〈代號〉欄——理由欄提到的代號不是退回（2026-08-25 修）

`qa-extract-worklist.py` 的 `rejected_slugs()` 原本對整份 `DECISIONS.md` 做
`re.findall`，**任何被提到的代號都算退回**。而退回理由欄最常寫的一句正是
「與 `X` 逐字相同，**保留後者**」——`X` 就是要留下來的那一則。
寫進理由欄的那一刻，它從審核頁上消失，而消失的樣子跟「本來就沒這則」一模一樣。

**第一批完全看不出來**：那幾個保留對象當時都已經 `approved:true`，
而 `approved` 在 `collect()` 裡先短路，輪不到退回名單判。
要等到「保留的那一則自己也還沒審」才會踩到——也就是第二批去重的當下。

修法是只取表格列裡的**第一個**代號（那一格就是〈代號〉欄），並跳過
〈保留待查〉與〈尚未審核〉兩節（那兩節列的是還要審的）。
實測影響 4 則：2 則是第二批要保留的，2 則是我在散文裡提到、其實沒動過的。
`qa-extract-selftest.py` 第 4 組釘住這件事，**退回修正會紅 4 項**。

> 這與 BOM 那個缺陷是**同一族**：兩者都讓待審項目安靜地少掉，而少掉沒有任何輸出。
> 這一族的共同解法是**對總數**——`DECISIONS.md` 的「共 N 則」與
> `worklist` 摘要的「還沒審 N」要能互相印證，對不起來就是有東西被吃掉了。

**同一天又漏一次，值得記下修法的演進**：第一版是用**節標題**排除
（跳過〈保留待查〉〈尚未審核〉）。當天稍後在同一節底下新增一張
〈保留但改了文字〉的表，5 則要保留的**又被算成退回**。
節標題是散文，寫的人想怎麼下就怎麼下；**表頭是欄位名，寫的人為了讓表讀得懂
本來就非寫對不可**。最終判定改成「這張表的表頭有沒有〈退回〉兩個字」。
兩次都是靠總數對不上發現的（135 vs 131、128 vs 123）。

###### 使用者第一輪逐則審：12 則裡退 7、改文字 5

反面案例漏洞**第三次**出現，而這次最嚴重：`RolePermissionSeeder.cs` 的註解在說
「**不**把 Q&A 助理掛在既有報表群組下，否則開燈那一刻每個作業員都會自動拿到權限」，
擷取物寫成「系統會自動為每個作業員賦予助理的權限」——**把一個刻意沒做的權限放寬
寫成了系統現行行為**。權限類的擷取物出這種錯，讀的人會對系統的存取範圍產生錯誤認知。

另外三則的錯法各不相同，湊起來剛好是這個來源專有的完整光譜：

| 代號 | 錯法 |
|---|---|
| `code-pur-goodsreceiptcontroller-9d78ffae` | **語意整個顛倒**——原文「作廢要回沖，**否則**額度被永久占用」→「PO 作廢會佔用採購額度」 |
| `code-ord-salesordercontroller-9feb7889` | **幻覺**——寫出原文從頭到尾沒有的「備品數量」（多半把 `StockQty` 讀成備品） |
| `code-core-approvalservice-8a62c9f6` | **把有用的那半句丟掉**——原文第二分句「代理設定仍在，重新儲存設定可再觸發」整句沒了，剩下的半句改寫成空話。使用者的判準是對的：先回頭查原文有沒有更清楚的內容，有就重寫、沒有才刪 |

###### 三種確定性殘留：`scrub()`，以及一個「把設計當缺陷修」的近失

同一輪使用者裁決全語料補掉，於是產生器新增 `scrub()`——純字面後處理、不呼叫模型，
掛在 `parse()` 的長度檢查**之前**（靠標籤字面撐到字數下限的說明不該放行）。
全語料套用一次改動 9 則。

| 樣態 | 盤點 | 成因 |
|---|---|---|
| 尾端棄權字元「無」 | 3 | 模型先給了完整答案，最後又補一個「無」回應第 6 條的棄權選項 |
| 殘留「說明：」 | 6 | 提示詞的欄位名被當成內容抄了進來 |
| 標題抄了整條章節位置 | 1 | 模型把提示詞裡的 `{heading}` 整條抄回標題 |

🚨 **第三項我先報成 7 則，那個數字是錯的**，而錯的方向很值得記：其中 5 則的標題
等於**章節位置的最後一節**，那是 `parse()` 第四種形狀**刻意的行為**——模型沒給標題時
用章節位置補一個標籤（「標題是標籤不是內容，從章節位置取一段不會憑空多出任何事實」）。
把它當殘留清掉，那 5 則會變成**沒有標題**。

**盤點一個「看起來不對」的樣態時，先去讀產生它的那段程式**——同一個外觀
（標題與章節位置重疊）底下，1 則是模型抄錯、5 則是設計如此，而外觀完全一樣。
`scrub()` 的自我測試把這條界線釘成兩項：清「標題含 `›`」的、不清「標題＝最後一節」的。


| | |
|---|---|
| 送出單位 | 193（來自 66 支檔案，清單見 `.claude/qa-extracts/code-sources.txt`） |
| 擷取 | **177 則**（172 首跑 ＋ 5 則補跑） |
| 模型棄權 | 82 段（30%）——**這是第 7 條在做事**，不是模型偷懶 |
| 失敗 | 首跑 5 → 修掉解析器後 **0** |
| 未產出 manifest | 3 支（整支的註解全是實作取捨，模型全數棄權） |

###### 兩項硬前置怎麼解的

**① 切段單位**：新增 `split_cs_sections`，單位＝**一個註解區塊 ＋ 它註解的那一行宣告**
（`///` XML doc 與 `//` 一般註解各自成塊）。宣告行一起帶進去是刻意的——
註解常用「它」「這裡」指涉下一行，只送註解會讓模型不知道在講什麼，然後開始猜。

**② 白名單**：`code-sources.txt` 進版控，**沒列的一律拒絕**；檔名命中紅線的**列了也拒絕**。
但真正的牙齒不在 Python：`QaCodeSourceGateTests.每一份程式碼manifest的來源都必須在清單上`
守的是「手寫一份 manifest 就繞過整道閘門」——那條路 Python 完全管不到，
而繞過的痕跡只有 `sourcePath` 那一行字。**這條測試實測會紅**（放一份指向清單外 `.cs`
的 manifest 進去，它就失敗），不是擺著好看的。

###### 第三件事是實作才浮出來的：章節位置也在紅線範圍內

`HeadingPath` 會直接印在引文展開的標題上（§10.11.12）。而 `.cs` 的天然章節位置是
**類別與方法名稱**——也就是提示詞第 3 條明令不准出現在擷取物裡的東西。
只擋內文、不擋章節位置，等於從側門把同一件事漏出去。

作法是 `模組中文名 › 註解的第一句話`，而那一句話要先過三關：XML 標籤、反引號、
括號、長度 3 碼以上的識別字全部拿掉。**優先找「本來就沒有識別字」的句子**，
而不是把識別字從第一句挖掉——挖掉的實測結果是「刻意不濾 ：小數位數…」，
句子破了，而破掉的句子會原封不動印給使用者看。整段找不到乾淨句子就**不收這一則**，
並列進審核頁的「未送模型」欄。

###### 實跑換來的五個修正（都是「內容對、格式不對就整則丟掉」）

| 症狀 | 修正 |
|---|---|
| 說明 37／25／23 字被 40 字下限擋掉（前 20 則就佔 3 則） | 程式碼來源下限降到 **20 字**。規格一段講一件事的來龍去脈，程式碼註解常常**一句話就是完整規則**。20 沒有理論根據，它是實測最短成立例子（23 字）的下緣，**真正的防線是人審** |
| 模型只回一句話、沒有標題行，也沒有冒號可切 | 收下內容，標題用**章節位置**補。標題是標籤不是內容，從章節位置取一段不會憑空多出事實，而叫模型再生一行反而是加料的機會 |
| 第一行 28 字被判「不像標題」，整則丟掉 | 同上：太長就代表那不是標題，是規則的第一句 |
| 「好的，以下是改寫：…」被冒號形狀切成標題＋內文，一路通過所有檢查 | 開場白詞表擋在冒號形狀前面。**這是既有的洞**，只是下限降到 20 之後更容易撞到 |
| 整支檔案全數棄權 → `entries: []` → 匯入器**整批中止** | 一則都沒有時**不寫 manifest**。這在 `.md` 那側一直沒踩到是運氣（每份規格都至少擷得出一則）；程式碼會經常踩到，而那是正確行為，不該因此炸掉整批 |

> ⚠️ 最後一項的後果值得單獨記：匯入器對空 `entries` 一律拋例外，症狀是
> **正式機所有規則類問題一起答不出來、而手冊照常** ——完全不像匯入壞了。

###### 端點：`llm-profile.env` 早就切好了，是腳本沒讀它

第一批跑之前 `11434` 不通、`11435` 通，而 `switch-llm.sh` 寫的 `llm-profile.env`
**已經是 `win`／11435**——app 那側早就切到 Win11 了。產生器卻只認 `QA_OLLAMA_URL`，
於是每一次呼叫都得手動再加一次 `--url`。同一台機器上「後端切到哪」應該只有一個事實來源。

兩項修正（2026-08-25，四個方向都實跑驗過）：

1. **端點解析多一層**：`--url` ＞ `QA_OLLAMA_URL` ＞ **`Features__QaAssistant__OllamaUrl`** ＞ 預設 11434。
   第三層就是 `switch-llm.sh` 寫的那個鍵，所以 `source ~/.cache/ollama-pull/llm-profile.env`
   之後，app 與擷取腳本走同一個端點。
2. **`preflight`**：跑第一段之前先確認端點通、模型在，不通就印出 `switch-llm.sh` 的用法。
   沒有它時的症狀是「每一段各自失敗一次」——193 段就是 193 行一模一樣的錯誤，
   而真正的原因只在第一行看得出來；**通道半開時更糟，每一段都會等滿 900 秒**。
   不通時**不自動改端點**，只把另一個常見埠探一下並照實說——自動切過去正是
   §17.11.7 那條「連得上不等於連對了」要防的事：兩份 Ollama 的模型庫不一樣。

###### 這一批收了什麼，記在哪裡

逐檔明細、產生條件、品質旗標分佈與過期風險記在
[`.claude/qa-extracts/CODE-INVENTORY.md`](../../qa-extracts/CODE-INVENTORY.md)（該檔日後併入
〈本地模型選型評估 — 嵌入與生成〉）。其中一項值得在規格裡也留一句：

> 🚨 **過期會來得比 `.md` 快很多。** 過期綁的是整支 `.cs` 的 SHA256，改一個空白字元
> 那支的擷取物就全部退出檢索。實測這 66 支裡 **54 支（84%）在近 30 天內改過**。
> 這一批的維護成本不在產生，在**跟著程式碼一起動**——`qa-extract-reconcile.py`
> 是唯一能把成本壓回「只重審變動的那幾段」的工具。

###### 品質：75.7% 無旗標，以及一個新的失敗型態

177 則逐則掃過四種可機檢的問題：

| | 則數 | |
|---|---|---|
| 無旗標 | 134 | 75.7% |
| 中文實作語彙（前端／回傳／欄位／參數…） | 29 | 16.4% |
| **疑似把「被避開的陷阱」寫成系統行為** | 9 | 5.1% |
| 章節位置是半句話 | 6 | 3.4% |
| 標題空泛（「規則說明」這種） | 4 | 2.3% |

**第三項是程式碼來源專有的新失敗型態，也是最貴的一種**：註解常在寫
「這樣做會出錯，所以不這樣做」，而改寫會把它變成「系統會出錯」——**意思正好相反**，
讀起來卻完全通順。實例：某則把「若靠環境過濾會一筆都撈不到」寫成系統現行行為，
而程式碼正是為了避免那件事才那樣寫的。

`DEV_VOCAB` 抓不到這一類（它抓的是英文識別字），提示詞第 7 條也擋不掉
（它擋的是「講實作取捨的整段」，不是「講反面案例的一句」）。
**目前唯一的防線就是人審**，量測腳本 `~/.cache/qa-code-batch1/quality.py` 只負責把
這 9 則標出來讓人先讀。要不要把這四項旗標併進 `qa-extract-worklist.py`，等審完第一批再說
——旗標會誤報，而誤報的旗標若被當判定，審核者很快就學會忽略整欄（§10.11.4 已經記過一次）。

###### 還沒做的

* **177 則一則都還沒核可。** `Approved` 是唯一防線，這一批的價值到人讀完為止都還是 0。
* 第二批（PUR／ORD／簽核／權限）沒開。等第一批審完，看退回率再決定要不要調提示詞。
* `.md` 側完全沒動：實測 457 則既有擷取物 **slug 漂移 0**、`headingPath` 全數對得回原文。

##### 裁決紀錄

**2026-08-25 使用者裁決：做**（原話「這個部分需要執行才能有完整的QA資訊」）。
待決 #50 的「開不開」至此結案，**剩下的是審核 177 則與後續批次**，該列繼續開著。
上面〈已執行〉那一節在裁決之後才寫，數字全部來自實跑，不是估的。

##### 第三批（2026-08-26）：MFG ＋ FIN 收尾，以及**永久排除 QA 桶**

使用者裁決「依建議進行」。三件事一起定下來：

**① 這一批收 MFG 62 支＋FIN 最後 1 支。** 選法與前兩批相同（0.45–0.65 缺口帶）。
它**明顯比第二批大**（第二批 41 支／109 單位／產出 140 則；這批 162 單位、
所選檔案切得出約 290 個單位，預估產出 200 則上下）。
不往下切一半是刻意的：MFG 是一個領域，切一半會讓「排程怎麼算」與「派工怎麼派」
分兩輪審，**審第一輪時看不到第二輪的對照，反而更難判**——
第二批那 5 組逐字重複就是靠同一輪並排才看出來的。

FIN 那 1 支（`Modules/FIN/Models/IErpSyncDocument.cs`）順手收掉。理由不是它重要，
是**留著會讓 FIN 永遠停在「快收完」**，而剩一支的成本趨近於零。
（順帶更正一則錯誤結論：2026-08-26 之前我在對話裡說過「尚缺 MFG／QCR／FIN」，
**FIN 其實第一批就收了 8 支**，只剩這一支。盤點要看 `scored.jsonl` 扣掉
`code-sources.txt` 已列的，憑印象講模組名會錯。）

**② QA 桶（158 單位／36 支）永久不收，不是排在後面。**
它是這套助理自己的實作（檢索管線、匯入器、閘門）。註解密度高使它在缺口帶排第二，
但**排名高不等於該收**：缺口分數量的是「這段註解在既有語料裡找不到對應」，
它量不到「使用者會不會拿這件事提問」。
「排程引擎怎麼算的」對使用者有意義，「檢索器怎麼寫的」沒有；
更糟的是**語料自我指涉**——問 Q&A 的行為，答案引用 Q&A 的原始碼註解。
要改這個決定，得先改 `code-sources.txt` 裡那段註解，不是默默補一批。

**③ 收完這批之後還沒收的**：RPS 79／27、Core 其他 74／24、ACT 23／11、
QCR 15／7、`_Shared` 15／5。QCR 最小，適合當第四批的收尾。

> 🆕 **後續**：QCR 與手機成了第四批，其餘全部成了第五批（69 支／193 單位，
> 含 `_Shared` 那 15 個單位——它們散在 Core 主體那一桶裡，不是獨立的一桶）。
> 缺口帶至此**只剩 QA 桶**，程式碼來源收口。

⚠️ **「Core 其他」這個桶名藏了手機**（2026-08-26 被使用者一問才查出來）：
手機 API 在 `Api/Mobile/` 底下，既不在 `Modules/` 也沒有自己的 `Modules/Mobile/`，
於是 `batch2.py` 的分類器把它們歸進「Core 其他」——**模組表上完全看不出有手機**。
這與「簽核 0 則是假零」是**同一個形狀的錯**：依目錄分桶，桶名就會漏掉不照目錄長的東西。
缺口帶可收 11 個單位：`MobileMfgController` 8、`MobileInvController` 2、`MobilePushController` 1；
`MobileAuthController`（2 個單位）**被 `CODE_REDLINE` 擋下**（檔名含 `Auth`），列了也照樣拒絕，
那是對的——它是登入路徑。手機的**使用者面**規則已經在語料裡（`mobile-pwa.md` 在 `.md` 那批擷取過），
缺的是後端行為那一半。建議與 QCR 一起當第四批。

###### 每批都要跑一次題庫並記下數字——**但現有的那支看不到擷取物**（2026-08-26 查證後修正）

`ManualRetrievalBenchmarkTests` 是**絕對數量**的閘門：它擋得住「排序壞了」，
擋不住「語料變大把召回稀釋掉」。已經實測過一次——手冊 149 頁時 Recall@1 是 16，
154 頁時掉到 14，**測試沒紅**。

⚠️ **但它的 `InitializeAsync` 只呼叫 `IDocIndexBuilder.RebuildAsync()`，
沒有呼叫 `IExtractManifestImporter`**——擷取物根本不在那個測試庫裡，
於是它量到的稀釋**只有手冊頁彼此之間的**，擷取物加了幾批它一概看不到。
而 `DocChunkFilter` 是兩個語料別的 OR、**兩邊在同一次排序裡競爭**，
所以「每批跑一次它」在字面上做得到，實際上量不到我們擔心的那件事。
`ManualRetrievalAnchorTests` 更是**刻意**把擷取物排除在候選之外（那是它的設計目的）。
真正量得到的只有 `ManualExtractImpactBenchmarkTests`，而那一支要跑 **1 小時 40 分**
（絕大部分花在生成，不在檢索），不可能每批跑。

**所以附帶條件改成兩層：**

* **每批**：照跑 `ManualRetrievalBenchmarkTests` 並記下數字。它是**手冊側的對照組**
  ——手冊沒變而它掉了，代表壞的是排序或前提，不是語料規模。
* **要看擷取物造成的稀釋，得先有一支「只做檢索、不做生成、且匯入擷取物」的變體**
  （預估：重建索引＋匯入＋嵌入 859 塊約 5 分鐘，35 題查詢嵌入不到 1 分鐘）。
  ~~**尚未實作，登錄在此**~~ → ✅ **2026-08-27 已交付**：`ManualCrossPageDiagnosisTests`
  的稀釋臂（`ManualCrossPageDiagnosisTests.cs:193-218`）就是它——只做檢索、不生成，
  對同一份 35 題直白題庫跑「有／無擷取物」兩臂，並印出擷取物的**佔格數**。
  首次量到的結果即推翻 §10.9 記著的數字（@1 掉 9 題而非 1 題，見 §10.11.15）。
  在它出現之前，擷取物側的稀釋是**看不見的**，
  這句話要留著——把「條件已滿足」寫成既成事實，比沒有條件更糟。
  ⚠️ 但它是 `LiveOllamaFact`：**沒有推論服務時會 skip 而不是紅**，
  所以「條件已滿足」的前提是有人真的帶著端點去跑它。

###### 第三批實跑（2026-08-26）：63 支／246 則，以及兩個「失敗其實不是失敗」

63 支跑完 **62 分鐘**（08:36–09:38），生成 `gemma4:latest` 經 SSH 通道打 Win11。
擷取 **246 則**、棄權 19、章節位置不可用 14、**有旗標 16 則**。
語料因此成為 **1020 段：核可 590、退回 184、待審 246**
（對帳：184＋246 ＝ 1020－590 ✓，這個等式是本流程唯一測得到「無聲缺陷」的地方）。

`run.sh` 記了 2 支失敗，**兩支都不是失敗**，兩支的成因也是同一個：
一則輸出短到過不了 20 字下限，讓腳本回非零。

* `MrpEngine.cs`：那則是「可取用的數量不能低於安全庫存的數值。」（18 字）。
  manifest **照樣寫出 30 則**，只是這一則沒進去。
* `OperationSequenceGuard.cs`：模型回的是「**無**」（1 字），而該檔只有這一個單位，
  於是**一則都沒擷到、刻意不寫 manifest**——空 `entries` 會讓匯入器整批中止，
  這是產生器既有的保護，不是這次加的。

那個「無」值得記：它正是 `scrub()` 第三種殘留（尾端棄權字元）的極端情形——
整則**只剩**殘留。`scrub()` 清完就成了空字串，長度閘門接住它。
**兩道防線在這裡是串聯而不是重複**：`scrub()` 管「殘留混在正文裡」，
長度閘門管「清完之後沒有正文」。少了後者，這一則會以空白說明的形式進 manifest。

**送審前兩道重複掃描都已先跑**（§10.11.13 記過「必須先跑再審」）：
來源層 9 組逐字重複**全部來自第一、二批且都已處置**，MFG 沒有新增；
extract 層逐字重複 **0 組**。⚠️ 但兩道比的都是字面，語意重複抓不到（待決 #48）。

###### 手冊側對照組（2026-08-26，第三批生成後）

`ManualRetrievalBenchmarkTests`，純詞彙、手冊 154 檔、**擷取物不在該測試庫裡**：

```
題數 35　Recall@1=19/35 (54%)　Recall@3=31/35 (89%)　Recall@5=33/35 (94%)　MRR=0.708
前 10 名內完全沒命中：StockShortage 這個畫面在做什麼
```

⚠️ **不要拿它跟上面那個「149 頁 16、154 頁 14」比**：那兩個數字量的是
**原始 30 題**，現在的題庫是 35 題（29 中文＋6 英文），分母不同。
記在這裡是要當**第三批之後的起點**，往後每批都記同樣這四個數字。
第三批的 246 則還沒核可，所以這一批對檢索的影響**還沒發生**——
真正要比的是那 246 則核可並匯入之後的同一組數字。

###### 第四批（2026-08-26）：手機 API ＋ QCR，10 支／34 則

擷取 34 則（**0 則有旗標**）、棄權 4、章節位置不可用 2、失敗 0。QCR 至此收乾淨。

**手機那 3 支重跑過一次，原因是章節位置的模組名錯了。**
`module_label()` 只認 `Modules/<代號>/`，而手機在 `Api/Mobile/` 底下，
於是 `headingPath` 前綴變成「系統共用」。詞彙通道拿 `HeadingPath` 當索引的一部分
（`Lexemes` ＝ `HeadingPath` ＋ `Text` 平鋪），**問「手機報工」永遠不會在標題上命中**。
改成 `PATH_MODULES` 前綴表（`Api/Mobile/` → 「手機」／manifest 代號 `mobile`）後重產三份
——當時一則都還沒核可，**沒有賠掉任何審核結論**（重產已核可的則會賠掉，見 `--sections` 的說明）。

**這是同一個形狀的錯的第三次**：依目錄分桶，桶名就會漏掉不照目錄長的東西。
① 第一版依目錄分桶時「簽核 0 則」是假零（簽核在 Core 主體）；
② 第三批的模組表把手機藏在「Core 其他」裡，直到使用者問「有沒有規劃手機」才查出來；
③ 就是這一次，同一個原因讓章節位置也標錯。
`PATH_MODULES` 是**分桶方法的補丁，不是特例清單**。

**送審前抓到一組跨批次語意重複，而且只有來源層掃描抓得到。**
`code-mobile-mobilemfgcontroller` 的首件檢查那則，與第三批**已核可**的
`code-mfg-dispatchcontroller-32647c3c` 出自同一段複製貼上的註解（§26 首件檢查閘口）。
extract 層逐字掃描回報 **0 組**——它比字面。抓到的是來源層掃描（比原始註解）。
**這是待決 #48 那件事的第一個跨批次實例**：需要語意近重複掃描，兩道字面掃描都不夠。

###### 改程式的語料代價：三份 manifest 過期，對帳把它縮到 9 段

加班那次改了 `WorkCalendarController.cs`、`ScheduleController.cs`、`mfg-domain.md`，
三份 manifest 立刻過期——**過期＝那些已核可的段落全部退出檢索，而且沒有任何訊息**。

| manifest | 沿用 | 重產 |
|---|---|---|
| `mfg-domain.json` | 154（其中 **110 已審，解凍**） | 1 |
| `code-mfg-schedulecontroller.json` | 17（其中 15 已審） | 1 |
| `code-mfg-workcalendarcontroller.json` | 0 | 7（該檔改得多，3 則原核可全數作廢） |

**這是「改程式的隱藏成本」的第一個實例**，也是 §10.11.3 對帳機制第一次在**程式碼來源**上發揮作用。

**對帳腳本自己有一個會靜默失敗的洞（同日修）**：`qa-extract-reconcile.py` 的 `--url`
預設寫死 11434、**不看 `QA_OLLAMA_URL`**，但它失敗時印的提示卻是
「要讓它在這個 shell 一直有效：`export QA_OLLAMA_URL=…`」。
照著提示做了再跑一次，它照樣打 11434，然後回報「產生器沒有產出 manifest（多半是連不上或參數錯）」
——**提示是對的，它自己不照做**。已改成與產生器同一條解析鏈。

**第 7 條的反面案例漏洞又出現一次（第五次），而且是在我當天寫的註解上。**
`code-mfg-workcalendarcontroller-e1d007dc`（已退回）：

> 原文：「改完不重建時段，排程就吃不到那個改動，**而且不會有任何錯誤訊息**」
> 擷取物：「即使沒有重建時段，排程結果**也會**反映該變動，並且系統不會顯示任何錯誤訊息。」

意思完全相反。值得記的是它發生在**刻意寫得很清楚的新註解**上——
可見這個漏洞**與原文寫得好不好無關，只要句型是「不這樣做會怎樣」就會翻**。

###### 靜默排除家族的第三個變體：表頭只能判一次（2026-08-26 修）

`rejected_slugs()` 的上一版是「只要 `harvest` 還沒開，就把這一列當表頭候選」。
於是表頭沒中之後，**資料列會繼續被當成表頭試**——只要某一列的理由欄裡出現「退回」兩個字，
`harvest` 就從那裡打開，**它下面的每一列都被算成退回**。
實際踩到：〈保留但改了文字〉表裡有一列寫著「舊的退回／爭議紀錄已作廢」，
於是它下一列那則要保留的被判成退回。

**症狀一如既往只有一個**：`DECISIONS.md` 的「共 N 則」與 worklist 的總數對不上
（預期 191、印出 192）。這個家族至此三次，**三次都只有總數對帳測得到**：
① BOM 造成的過期、② 理由欄提到的代號被當成退回、③ 這一次。
修法是進表時只讀第一列非對齊列當表頭，表頭不含「退回」就整張表跳過直到表結束；
`qa-extract-selftest.py` 補了一個直接針對這個形狀的案例（拔掉修正會紅 2 項）。


###### 第五批（2026-08-26）：把缺口帶收乾淨，69 支／缺口帶 193 單位

範圍＝缺口帶（0.45–0.65）**扣掉 QA 桶之後剩下的全部**：
RPS 27 支／79、Core 主體 24／75、ACT 11／23，加上五個一到兩支的尾巴
（單號 1／8、稽核 2／3、報表產出 1／2、簽核 2／2、背景排程 1／1）。

**連一支一單位的尾巴也收**，理由同第三批收 FIN 最後一支：留著會讓每一桶
永遠停在「快收完」，而剩一支的邊際成本趨近於零。收完之後缺口帶只剩 QA 桶
（158 單位／36 支，永久排除），**程式碼這個來源到此收口**。

###### 這一批抓到一個比「擷取失敗」嚴重的失敗形狀：**棄權被當成內容**

`Filters/PlantFormEnhancementFilter.cs:146` 產出了這一則：

> 曾因此而失效｜規則判斷：這段註解描述的是程式碼如何處理表單資料的技術細節
> （例如使用 `Any` 而非 `FirstOrDefault` 的原因…），屬於開發實作取捨和底層機制說明。**輸出：無**

**模型的判斷完全正確**——那一段正是第 7 條該擋掉的實作取捨。它照約定棄權了，
只是把「無」寫在最後一行，而 `parse()` 的棄權偵測**只認開頭**
（那道 pattern 的註解白紙黑字寫著「pattern 只認開頭」，理由是不想誤傷正常擷取物）。
於是整段自我說明變成「說明」、章節位置變成「標題」，
**一路通過標題長度、字數下限、`scrub()` 每一道檢查**，長得跟正常擷取物一模一樣。

**這比擷取失敗嚴重**：失敗會計數、會列在審核頁上被看到；棄權被當成內容不會留下任何痕跡，
而且產出的東西看起來很正常——除非有人去讀它的內容，發現裡面在講 `Any` 和 `FirstOrDefault`。
它是靠掃「模型自述用語」（`規則判斷`／`輸出：`）掃出來的，不是靠任何一道既有檢查。

修法：加認收尾的棄權標記（`輸出：無`／`結果：無`／`判定：無`／單獨一行的「無」），
**只認明確標記，不猜語意**——猜語意會開始丟掉正常擷取物，那是往反方向的錯。
`qa-extract-selftest.py` 釘住三種收尾寫法必判棄權、三則正常擷取物不得誤殺，
而且斷言分**三種**結果而不是兩種（棄權／擷取到／算成失敗）：
「算成失敗」也不算通過，因為失敗數正是用來決定哪些檔案值得重跑的訊號。
拔掉修正會紅 1 項，且訊息直接指出那一則是「擷取到」而不是「算成失敗」。

該檔重產後 `:146` 正確棄權（4 則、棄權 2），全批待審數 231 → 230。

###### 這一批開出了第三道紅線：`CODE_REDLINE_BODY`（段落層）

`Program.cs` 在缺口帶有 9 個單位，排 Core 主體第二。它原本收不了，
而**收不了的理由是既有兩道紅線都比對檔名**：

| 道 | 比對什麼 | 擋得住 | 擋不住 |
|---|---|---|---|
| ① `code-sources.txt` | 檔案路徑（逐檔列舉、預設拒絕） | 沒被審過的檔進語料 | 已列檔案裡的敏感段落 |
| ② `CODE_REDLINE` | **檔名** | `LicenseValidator.cs` 被手滑列進清單 | 同上 |
| ③ `CODE_REDLINE_BODY` 🆕 | **段落本文** | 檔名乾淨但內容在講授權／金鑰的那一段 | — |

`Program.cs:665` 是整段「啟動時授權驗證」，含 Internal License 的 clientCode，
而 `Program.cs` 這個檔名一個字都不命中 ①②。
**過濾的維度與內容所在的維度不同，就一定漏**——這是同一個形狀的錯的第四次
（簽核假零、手機被歸進 Core 其他、退回名單表頭連坐，加這次）。

實跑攔下 5 段，全部攔對：`Program.cs` 的 :96（DataProtection 金鑰持久化）、
:342（健康檢查對匿名者收斂 license LastError）、:665（啟動授權驗證）、
:744（驗證失敗即終止），以及 `AuditPiiMasker.cs:3`（提到 `PasswordHash`／`SecurityStamp` 整欄排除）。
最後那則有一半是使用者規則（「稽核不留明文個資」），**這一則是誤傷**——
但誤傷的代價是少一則、而且會印出來，漏放的代價是授權內部細節躺在任何登入者查得到的語料裡。
兩邊不對稱，所以規則寧可寬。

三件實作上刻意的事：

* **本文一律不印**（終端機與審核頁都只印檔案行號）。把攔下來的段落印進 log
  或審核頁，等於換一個地方外洩，紅線就白設了。
* **不沿用 `CODE_REDLINE` 的字**。那條是為檔名寫的，拿去比對本文會讓
  `Auth`（`DataAuth` 資料權限）、`Token` 整批連坐，而「誰看得到哪些單」
  正是使用者最需要的規則。本文那條收窄成「講的就是機密機制本身」，中英各列一份
  ——註解是中文寫的，只比英文識別字會漏掉整段用中文描述授權流程的段落。
* **CI 也補了一道**（`QaCodeSourceGateTests` 第三條 [Fact]）。Python 那道守
  「跑產生器」，CI 這道守 **manifest 本身**——manifest 是人手改的檔案，
  一則擷取物的文字可以在人工改寫時被改成任何東西。掃 1058 則命中 **1 則**：
  `code-mobile-mobilepushcontroller-c0a05efb`（推播訂閱被別的帳號佔用時綁不上去，
  「金鑰」指的是 Web Push 訂閱自帶的公開參數，不是系統機密）。
  **它進的是逐則列舉的 `RedlineBodyAllow` 例外表，不是把規則改寬**
  ——改規則去迎合現有資料，是閘門無聲腐爛的標準路徑。
  ✅ **2026-08-26 使用者裁決：退回**（不留例外）。`RedlineBodyAllow` 那張例外表因此
  **目前是空的**——這是刻意的結果而非巧合：留下唯一一筆例外，等於替日後每一筆例外開了先例。

`qa-extract-selftest.py` 補了正反兩面（4 段機密必攔、5 段正常規則不得連坐，
再加一次真的切 `Program.cs` 確認 main() 有用到它）；把紅線收窄回檔名版會紅 2 項。

###### 第五批的裁決結果：224 核可、8 退回，**程式碼來源到此收口**

兩輪。第一輪我逐則核對原文後提出建議，使用者裁決「依建議進行」：**退回 5、改寫 8**。
第二輪使用者翻審核頁後裁決「除下列外均 OK」：**再退 3、其餘 224 整批核可**。

退回的 8 則分成三類，每一類都是既有機器檢查抓不到的：

| 類 | 則數 | 例 |
|---|---|---|
| 內容錯（反面案例寫成系統行為、字義錯、方向顛倒） | 1 退＋5 改寫 | `82fb08ee` 把 xmin 防止的「後到者覆蓋先到者」寫成「以最後寫入為準」——**讀起來像在保證正確** |
| 語意重複（字面完全不同，兩道逐字掃描各跑一次都 0 組） | 3 | `bc1c26fc`↔`67b5bdf6`、`a7b12b4f`↔`a1fbf067`、`699634fd`↔`e66c1541` |
| 內容不完整或非使用者規則 | 4 | `8af99eb8` 只剩理由那一半、`874b8800` 標題截斷、`4b517759` 是程式怎麼運作 |

⚠️ **第 7 條的反面案例漏洞這批 3 次，累計 6 次，而且三則全部落在 `xmin` 並發註解上。**
那種註解的寫法天生就是「不這樣做會怎樣」——這是六次以來第一次找得到共同的高發區，
若日後要收窄提示詞或加旗標，這是唯一有跡可循的切入點。

⏸ **另一個形狀，待做旗標**：原文是「**X 必須 Y，否則 Z**」，而擷取物只剩 Z 那半段，
規則整條掉了。第三批 `afcbf207`、`2d453c20`，第五批 `8af99eb8`，**累計三次**。
它與第 7 條的漏洞**不是同一件事**：那個是把 Z 寫成系統行為（內容錯），
這個是 Y 整條不見（內容不完整但沒有錯），所以「內容與原文相符」這種檢查抓不到它。
但它有跡可循——**擷取物以「這樣做」「因此」「所以」起手就是徵兆**：
那些詞需要先行詞，而先行詞正是被丟掉的那一半。做成旗標的成本很低。

語料 **1288 段：核可 1088、退回 200、待審 0**。
匯入實跑（`SmartFactoryMES_QaBench`）：manifest **250 份**、寫入 1288 段、已核可 1088、過期 0。

###### 「只剩災害、沒有規則」的旗標：做出來了，但**原本設想的偵測方式是錯的**

我原本說徵兆是「擷取物以『這樣做／因此／所以』起手」——承接詞需要先行詞，
而先行詞正是被丟掉的那一半。**掃全語料 1288 則只命中 1 則**，就是已經退回的 `8af99eb8`。
三次實例裡的另外兩次（`afcbf207`／`2d453c20`，原始版本從 git 取回比對）**根本不是這個開頭**，
它們是「如果沒有 X，就會 Z」。

改掃條件否定開頭：命中 36 則，**其中 35 則是完全成立的規則**
（「如果沒有指定時區，就用使用者的時區」——那是預設值規則）。開頭不是訊號。

真正分得開的是**後果是「系統的動作」還是「一個災害」**：

| | 開頭 | 後果 | 判定 |
|---|---|---|---|
| 正常 | 如果沒有指定 X | 系統**會用 Y** | 預設值規則，該收 |
| 有問題 | 如果沒有 Y | **會導致**庫存永久鎖住 | 只剩災害，規則不見了 |

所以條件否定與災害詞**兩個都命中**才標記。實測 1288 則命中 21 則，**精確率約兩成**
——其餘八成是「規則 ＋ 為什麼」，那是好的擷取物。旗標的價值在於把要用力讀的
從 1288 則縮到 21 則，不在於它判得準；它是旗標不是閘門。

**它抓到一則已核可的漏網**：`code-inv-stockadjustmentcontroller-03b8f8a2`。
原文（`StockAdjustmentController.cs:212`）是純粹的實作陷阱說明
（EF 的 `RemoveRange` 不從記憶體集合移除，所以要 `Clear()`），
擷取物寫成「系統會導致重複過帳」——**沒有任何使用者規則，而且把一個已修掉的缺陷
寫成系統行為**。同一句註解的另外兩份複製（`598a81cd`／`9bce17d8`）第一批就退回了，
**這第三份文字不同，逐字重複掃描抓不到，於是通過了核可**。
✅ **2026-08-26 使用者裁決：退回**（`03b8f8a2`）。
另 `code-mfg-materialslipcontroller-eddaa9c7` 內容成立但標題是「規則說明」，
**改標題後於 2026-08-27 核可**（見 §10.11.14）。

`qa-extract-selftest.py` 釘住三則已知實例必標、三則正常預設值規則不得標，
並且**把「承接詞開頭那一種標不到」寫成斷言**——免得日後有人再想一次那條死路。
拿掉災害詞的條件會紅 1 項（三則正常規則全被誤標）。


##### 兩件已知但沒處理的：現在的處置

###### ① 語意相同、字面不同的重複 → 併進待決 #48 的全盤篩檢，**不另開一輪**

實例：`code-inv-slowmovinginventorytool-58ca6910` 與
`code-inv-inventoryvaluationtool-ee05ee52` 講同一件事、用不同的字寫，
**兩則都已核可**。來源層與 extract 層的逐字掃描都抓不到——兩道掃描比的都是字面。

作法：對所有 `approved:true` 的擷取物兩兩算餘弦（snowflake 向量已經在庫裡，
不必額外跑模型），輸出**排序後的前 N 對**當「疑似重複」清單給人看。

兩件要先講明白的：

* **不訂門檻，輸出排序清單。** 相關度門檻在 S2 就訂不出來（§10.10），
  這裡是同一個問題。人看 30 對，比人調一個訂不出來的門檻實際。
* **不做成閘門、不自動退回。** 同一條規則在兩個模組各講一次**有時候是對的**——
  使用者從哪個模組問都該找得到。所以它是清單，不是測試。

為什麼併進 #48 而不是現在做：#48 那次本來就要重嵌全語料、要調權重，
而近重複掃描吃的正是同一批向量。分開做等於嵌兩次、調兩次。

✅ **2026-08-26 已執行完畢，結果與裁決見 §10.11.14。**
先記一句在這裡免得只讀本節的人以為它還沒做：34 群裡 **23 群判「都留」**，
**做成閘門的話會誤殺七成**。

###### ② 延遲 93% 在 prefill → 能動的只有「少餵 token」，三個方向兩個要等 #48

§11.2 實測：93% 的延遲在 prefill，CPU 約 85 prompt tokens/秒，
5 塊 context 光讀進去就 45 秒。串流已經做了，它只搬得動那 7%。
硬體不會變（無獨顯），所以剩下的全部是「餵少一點」：

| 方向 | 預期 | 為什麼現在不做／可以做 |
|---|---|---|
| 降 `topK` 5→3 | prefill 近乎線性，省約 4 成 | **等 #48**。語料還在長，現在調的值到最後不會是對的，而且會跟篩檢的權重調整互相污染 |
| 縮短塊長 | 同 `topK` 下餵進去的 token 更少 | **等 #48**。要全語料重切重嵌，且「改一小段字塊數可能跳七塊」 |
| prefill 快取（固定前綴重用 KV） | **實測否決**，見下 | 與 #48 無關、唯一不必重嵌，故先量它——量完的結論是不做 |

###### prefill 快取實測（2026-08-26）：**自己做前綴快取是死路，但量到一個更大的東西**

`~/.cache/qa-prefill/measure.py`／`measure2.py`，`gemma4:latest` 經 SSH 通道打 Win11，
`num_predict=1`（要量的是 prefill，讓它生成完會把 93/7 那個比例重新混進來）。

| | prompt | prefill | tok/s |
|---|---|---|---|
| A 只有系統提示詞 | 343 tok | 0.18s | — |
| B ＋5 塊 context（2758 字） | 2423 tok | 26.9／27.9s | 86.8–90.0 |
| C 與 B **一字不差**再打一次 | 2423 tok | **0.10s** | 23764 |
| D 前綴同 B、**只換最後那句問題** | 2425 tok | **0.34s** | 7221 |
| E context 全換、系統提示詞不變 | 3087 tok | 38.2s | 80.9 |

**① 自己做前綴快取沒有意義，這一項就此關掉。**
系統提示詞只佔 343/2423 = **14.2%**，那是理論上限；而 E（只有系統提示詞相同）
量到 80.9 tok/s，與 B 的 86.8 幾乎一樣——**那 343 token 省不到**。
上限 14% 又拿不到，不值得為它寫任何程式。§11.2 那三個方向裡的第三個，**否決**。

**② 但 D 是這次真正的收穫：Ollama 重用的是「共同前綴」，不是「整段相同」。**
context 一模一樣、只換結尾那句問題，prefill 從 27.9 秒塌到 **0.34 秒（省 98.8%）**。
而 `BuildUserPrompt` 現在的排版本來就是「參考資料在前、問題在最後」——
**這個排版是拿得到 D 的前提，不要為了可讀性把問題搬到前面。**
（沒有人打算搬，記在這裡是因為那會是一個沒有任何錯誤訊息的效能回歸。）

D 的用處在**同一輪對話的第二次以後**：追問若沿用上一輪的 context，
那一次的 prefill 是 0.3 秒而不是 30 秒。反過來說，**每次追問都重新檢索一次，
代價就是每次都付滿 30 秒**。§11.3 的回顯確認尤其明顯——確認步驟本來就不該重新檢索。
⚠️ 這是**量到的機制**，不是已實作的行為；要不要沿用 context 是 §11.3 的設計問題，
沒有在這一輪動它。

**③ 順帶把「少餵 token」量化了**：B 2423 tok→27.9s、E 3087 tok→38.2s，
大致線性，**每多 1000 個 prompt token 約多 12 秒**。
所以降 `topK`／縮短塊長仍然是主要的槓桿（兩者都等待決 #48），
而 §11.2 量到的 85 tok/s 這次複驗為 80.9–90.0，**沒有漂**。


###### 待決 #48 之一「詞彙通道標題加權」：**做了，但實測把預設值推向反方向**（2026-08-26）

程式面照 #48 原本寫的作法落地了——`DocChunks` 拆成 `HeadingLexemes`／`Lexemes` 兩欄，
查詢期 `setweight(…,'A') ‖ setweight(…,'D')` 後以權重陣列 `ts_rank`
（migration `AddDocChunkHeadingLexemes`，權重可由 `Features:QaAssistant:LexicalWeights` 調）。
**但 #48 沒有寫的是權重要設多少，而那個數字決定一切。**

`ManualLexicalWeightSweepTests` 一次跑完兩個語料臂 × 六組權重 × 35 題（純詞彙通道）：

| 標題:內文 | 只有手冊 686 塊 | | | ＋核可擷取物 1065 段 | | |
| --- | --- | --- | --- | --- | --- | --- |
| | @1 | @5 | MRR | @1 | @5 | MRR |
| **等重** | 19 | **33** | 0.708 | 18 | **32** | 0.669 |
| 1.5 倍 | **28** | 33 | **0.862** | **24** | 31 | **0.775** |
| 2 倍 | 27 | 33 | 0.843 | 23 | 29 | 0.752 |
| 3 倍 | 26 | 32 | 0.812 | 23 | 27 | 0.711 |
| 4 倍 | 25 | 31 | 0.794 | 21 | 26 | 0.656 |
| **10 倍（PostgreSQL 預設）** | 25 | 29 | 0.774 | **16** | **23** | **0.535** |

**① 直接照抄 PostgreSQL 的預設權重會比不加權更糟。** 10 倍那一列在正式語料上
@1 16／@5 23，比等重的 18／32 全面難看，11 題掉出前 10 名。
那組值是為英文文件的 title／body／abstract 標記調的，這裡的「標題」不是那個東西——
擷取物的標題是規則標題，專有名詞密度遠高於手冊麵包屑，**把標題加重等於把 1065 段規則整批推到手冊前面**。

**② 逐題看才知道加權在交易什麼：它拿口語題換直述題。**
「派工作業怎麼報工」「MRP 執行要怎麼跑」這種說得出功能名稱的提問，加權後一律升到第 1 名；
而「哪些客戶買最多」10→0、「倉庫之間要移轉庫存怎麼做」2→3→8→0、
「不良品要報廢或重工怎麼處理」5→8→9→0 這種**使用者不知道功能正式名稱**的提問，
加權越重掉得越兇。機制很直接：口語的用字只出現在內文、不出現在標題，
壓低內文權重就是把這一類整個犧牲掉——**而題庫刻意大量收錄的正是這一類**
（`ManualRetrievalBenchmarkTests` 的註解：「那才是使用者真正會打的字」）。

**③ 判準是 @5 不是 @1。** 詞彙通道只在語意通道不可用時當唯一通道用，
那時 topK 塊會**全部**進 prompt——正解排第 1 還是第 5 對模型沒有差別，掉出 topK 才是答不出來。
照這個判準等重最好（@5 32、只有 1 題掉出前 10）。
**於是預設值訂為 `1,1,1,1`：能力做好了，但預設不開。**
日後若改以詞彙為主通道，建議值是 1.5 倍（MRR 0.775 vs 0.669，@5 只差 1 題），改之前重跑那支掃描。

**④ 這一輪順帶把稀釋量出來了**，而它比 #48 當初估的溫和：
同一組權重，擷取物讓等重臂的 @1 掉 1 題（19→18）、@5 掉 1 題（33→32）。
**但加權會放大稀釋**——1.5 倍時擷取物讓 @1 掉 4 題（28→24），10 倍時掉 9 題（25→16）。
也就是說 #48 想用來對付稀釋的那個手段，本身對稀釋更敏感。

⚠️ **兩個沒有解決的東西照實記著**：
`ManualRetrievalBenchmarkTests` 的門檻（@5≥30／@3≥27／@1≥15）在等重與 1.5 倍下都過，
**它仍然分不出「排序壞掉」與「語料長大」**；而 `HeadingLexemes` 全空的舊格式索引
會讓加權安靜地不生效，那道防線是 `ManualIndexIntegrationTests.有標題的塊必須有標題切詞`
加上摻進 `ContentHash` 的 `DocIndexBuilder.IndexFormatVersion`——
沒有後者，增量重建看 hash 沒變就一列都不會重做。

###### §10.11.17 英文題庫 6 → 23 題：**飽和的尺量不到的東西，換一把就量到了**（2026-08-31）

G4 在 2026-08-27 裁決把 `ExtractCrossLang` 預設翻成 `true` 時，留了一句
「**代價那把尺已飽和，英文題庫擴大之後應該重跑一次**」。這一輪把它做完。

**題庫怎麼擴的（方法比數字重要）。** 新增 17 題英文，正解頁與既有 17 題中文題**完全相同**
——中英同頁並列的**配對設計**。這樣量到的差只能歸因於語言，不能歸因於
「英文那幾題剛好比較好答」。隨機挑頁做不到這件事，而**挑得順手一點就會挑出一份天生飽和的題庫**
（定案 #40 的老問題，換個地方又出現一次）。
題目一律從**英文頁的實際內文**寫出來（逐頁讀 `DocChunks` 的 en 塊），
不是把中文題送去翻譯——翻譯會把中文標題的用字帶進來，變成在量翻譯一致性。

**量到的結果：舊尺看不見的代價，新尺看見了。**

| | 開關 OFF | 開關 ON（現行預設） |
|---|---|---|
| 英文規則題 6 題 | 命中規則段落 **0**（命中任一正解 2） | **6** |
| 英文手冊題 **23** 題 Recall@1 | 19 | 19 |
| 英文手冊題 **23** 題 Recall@5 | **21** | **20** |
| 英文手冊題 MRR | 0.857 | 0.835 |
| 擷取物佔格 | 0/115 | **21/115（18%）** |
| 中文手冊題 29 題 | @1 18／@5 28／MRR 0.759 | **完全相同** |
| 中文規則題 6 題 | 6 | **完全相同** |

舊的 6 題英文尺是 **6/6、MRR 1.000**，兩臂一模一樣，於是當時只能寫「沒退步」。
23 題之後**代價現形：@5 掉 1 題、MRR 掉 0.022**。
交易仍然划算（換 6 題規則題全中），**但這一次是有證據的划算，不是量不到的划算**。
中文那兩列兩臂完全相同，證明改動的作用域是對的（測試自己的註解：不同就是 bug 不是效果）。

**順帶量到：英文在詞彙通道上並不比中文差。** 52 題純詞彙臂分語言看，
中文 29 題 @1=14／@5=26，英文 23 題 @1=11／@5=20——**@1 命中率都是 48%**，
英文甚至沒有任何一題掉出前 10（中文有 1 題）。
`CjkTokenizer` 對 en 走 `english` 組態、對 zh 走 `simple` ＋重疊雙字，兩邊各自成立。

🚨 **這一輪最該記的不是上面任何一個數字，是差點漏掉的那一步。**
`ManualRetrievalBenchmarkTests` 的門檻是**絕對題數**（`top5 >= 30` 這種），
而題庫從 35 題變成 52 題。**門檻不動的話，@5 的容忍度會從「可掉 2 題」變成「可掉 16 題」
——而且測試照樣全綠，沒有任何訊號。** 加題庫與升門檻必須同一次做完。
已重訂為 43／37／22（實測 46／41／25，餘裕 3–4 格與原本一致）。
⚠️ 同時注意百分比會下降（@5 94% → 88%）而**那不是退步**，是題庫組成變了；
要看退步請看新加的分語言兩列。這正是 #48 記著的「門檻分不出『排序壞掉』與『語料長大』」
的同一個病灶，只是這次換成「題庫長大」。

🚨 **第二個連帶影響，比門檻那件事更難察覺：`Bank` 被五支測試共用。**

| 測試 | 規格裡記著的表 | 現況 |
|---|---|---|
| `ManualLexicalWeightSweepTests` | 待決 #48 之一的六組權重表 | 量自 **35 題** |
| `ManualCrossPageDiagnosisTests` | §10.11.15 稀釋臂、§10.11.16 分艙／topK 兩張表 | 量自 **35 題** |
| `ManualContextBudgetTests` | 參考資料預算 | 量自 **35 題** |
| `ManualCrossLangExtractTests` | §10.11.17（本節） | **已用 52 題重跑** ✅ |
| `ManualRetrievalBenchmarkTests` | 本節的門檻 | **已重訂** ✅ |

**上表前三列的數字沒有失效，但它們與日後重跑的結果不再可比**——
同一支測試、同一個結論欄位，分母卻是兩個不同的題庫。
拿新舊兩次直接相減會得到憑空生出來的「退步」或「改善」。

**刻意不回頭重跑那三支**：它們的**結論**都不是絕對題數，而是相對排序
（等重 vs 加權哪個 @5 高、topK=8 vs 分艙 N=5 哪個便宜），
換一份題庫不會翻轉那種比較。真正會被咬到的是「拿 35 題的數字跟 52 題的數字比大小」，
所以要防的是**誤讀**，不是重算——故在此登錄，而不是把三張表作廢。
⚠️ 日後若要重跑其中任何一支並與規格對照，**先確認那張表的題庫大小**。

---

###### 待決 #48 之一的**重跑**：語料定版＋題庫 52 題後，**聚合結論翻轉，但那是我自己造成的**（2026-08-31）

#48 押後的理由是「等程式碼來源擷取完、語料定版再做一次全盤篩檢」。
擷取物已於 2026-08-27 收口（待決 #50 結案，核可 1070 段），故重跑。
**跑在自己開的 `SmartFactoryMES_QaSweep`**——這支測試的第一臂會刪光擷取物再重匯，
重匯出來的是**沒有向量的新列**，跑在 `QaBench` 上等於丟掉 1288 段嵌入（約兩小時重嵌）。
端點指向一個**關閉的**埠而非不通的位址：純詞彙掃描不需要模型，
連線被拒是立刻失敗，黑洞位址則會每一段等滿逾時。

**第二臂（正式語料：手冊 686 塊＋擷取物 1288 段／核可 1070）**

| 標題:內文 | @1 | @3 | @5 | MRR | 前 10 名外 |
|---|---|---|---|---|---|
| **等重** | 25 | 41 | 46 | 0.645 | 1 |
| **1.5 倍** | **31** | **43** | **48** | **0.728** | 2 |
| 2 倍 | 29 | 42 | 45 | 0.699 | 2 |
| 3 倍 | 28 | 36 | 41 | 0.647 | 6 |
| 4 倍 | 24 | 32 | 37 | 0.568 | 11 |
| 10 倍 | 17 | 27 | 32 | 0.447 | 15 |

**照聚合看，1.5 倍在三欄全勝，包括原本訂為判準的 @5（48 vs 46）——與 2026-08-26 的結論相反。**

🚨 **但那個翻轉是題庫組成造成的，不是檢索行為變了。** 分語言拆開：

| Recall@5 | 等重 | 1.5 倍 |
|---|---|---|
| 中文 29 題 | **26** | 25 |
| 英文 23 題 | 20 | **23** |
| 合計 | 46 | **48** |

**兩種語言的方向是相反的，而且各自都與既有解釋一致**：
中文題大量收錄「使用者不知道功能正式名稱」的口語提問，那些字只出現在內文、不出現在標題，
壓低內文權重就是犧牲它們（2026-08-26 §②已經記過這個機制）；
英文題則相反，英文標題是真的詞、會 stemming，加權吃得到。

**聚合之所以翻轉，是因為英文題在題庫裡的占比從 6/35（17%）變成 23/52（44%），
而這套系統的使用者以繁體中文為壓倒多數。** 換句話說——
**我為了量 G4 的代價而擴大英文題庫，順手把這把尺變得不再代表真實流量。**

⚠️ **由此得到一條要留著的規矩：這份題庫的「合計」列，不可用來做語言之間互相取捨的調校決定。**
它現在同時服務兩個目的（回歸守門、G4 的英文成本尺），而這兩個目的對組成的要求不一樣。
守門用合計沒問題（它擋的是程式改壞，不是使用體驗）；
**調權重這種會在語言之間交易的決定，一律看分語言那兩列。**

**@1 的方向則兩種語言一致**（中文 14→18、英文 11→13，1.5 倍都贏）。
所以真正的取捨是：**1.5 倍以中文 @5 掉 1 題（26→25，噪音等級）換取英文 @5 升 3 題與兩語言的 @1／MRR 全面改善。**
判準是 @5 的理由仍然成立（詞彙通道只在語意通道不可用時當唯一通道，topK 全部進 prompt，
排第 1 還是第 5 對模型沒差，掉出 topK 才答不出來）——**而在中文上，@5 判準說的是等重。**

✅ **2026-08-31 使用者裁決：維持等重（`1,1,1,1`），待決 #48 結案。**
兩次量測、兩份題庫、同一個判準，結論一致：**能力做好了，預設不開。**
加權的能力（兩欄化＋`setweight`＋`LexicalWeights` 旋鈕）留著，
**日後若英文使用者比例真的上來，改一個組態值即可，不需要再改程式**——
建議值 1.5 倍，改之前重跑這支掃描並**看分語言那兩列，不要看合計**。

---

###### 待決 #48 之二「降 `topK`／縮短塊長」：**兩件都不做，因為前提已經變了**（2026-08-26）

`ManualContextBudgetTests`（只檢索、不生成）量的是「實際餵進去多少字」。
先把字換算成秒——**不用估的**：拿真實 zh 手冊文字打 `gemma4:latest` 量了一次，
**2383 字 ＝ 1693 prompt token（0.7105 token/字）、prefill 18.47 秒 ＝ 91.7 tok/s**，
與 §11.2 的 85 tok/s（複驗 80.9–90.0）一致。系統提示詞固定 627 字。

| topK | 參考資料 | ＋系統提示詞 | prompt token | prefill | 35 題命中 | 難題全中 | 難題至少一個 | 跨頁題全中 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **5（現行）** | 2613 字 | 3240 | 2302 | **25.1 秒** | **34/35** | **39/53** | **47/53** | 4/12 |
| 4 | 2119 字 | 2746 | 1951 | 21.3 秒 | 34/35 | 38/53 | 46/53 | 4/12 |
| 3 | 1570 字 | 2197 | 1561 | 17.0 秒 | **32/35** | 38/53 | **45/53** | 4/12 |

**① 「省約 4 成」在比例上大致成立，但絕對值只剩 8 秒。**
5→3 省 8.1 秒，而**現在一則問答的總延遲約 27 秒（25.1 prefill ＋ 約 3 秒生成），
不是 §11.2 記的 48.5 秒**。差別在語料組成：擷取物平均 97 字、手冊塊平均 647 字，
而擷取物已經佔掉 topK 名額的 **40%**（三個 topK 值都是 39–41%，很穩定）。
§11.2 那 48.5 秒是擷取物還沒進語料時量的——**這一項存在的理由自己縮水了一半。**

**② 5→4 不是免費的，而 35 題那一份會告訴你它是。**
35 題題庫在 5 與 4 都是 34/35，看起來白賺 3.8 秒；
但難題題庫（定案 #40）兩個指標各掉 1 題（39→38、47→46）。
**這正是定案 #40 說「只信沒飽和的指標」的用途**——直白題庫在這裡已經飽和，量不出代價。

**③ 於是維持 `topK = 5`，不降。**
5→4 買到 3.8 秒（總延遲的 14%）而難題掉 1 題；5→3 買到 8.1 秒（30%）而 35 題掉 2 題、難題掉 2 題。
使用者感覺不出 27 秒與 23 秒的差別，但答不出來會知道。

**④ 縮短塊長同樣不做，而且理由比「不划算」更硬。**
6000 字的參考資料預算在 35 題裡只丟掉 **1 塊**（最長 6051 字），平均 2613 字離上限很遠——
**預算根本不是瓶頸，所以調它一點用都沒有**。而切短塊會讓同一頁需要更多塊才講得完，
等於把省下的字數用更多 topK 名額換回來；代價是全語料重切重嵌，
且已知「改一小段字塊數可能跳七塊」。

**⑤ 順帶量到一個與 topK 無關的既有缺口：跨頁題 12 題裡只有 4 題「全部正解都在 topK」，
而且 topK 5／4／3 完全一樣。** 跨頁題的瓶頸不是名額不夠，是檢索根本沒把第二頁排進來。
**這裡不修**——它屬於檢索品質，不屬於 #48 的延遲議題，記在這裡是因為
「降 topK 會傷跨頁題」這個很自然的假設**被這份資料否定了**，日後不必再繞回來假設一次。

###### 順帶修掉的：匯入器無條件重寫，把向量一起清掉（2026-08-26）

跑上面兩輪量測時，同一批 1288 段被重嵌了**三次**，每次約 30 分鐘。
原因是 `ExtractManifestImporter` 對每一份 manifest 都無條件「刪該份的列 → 寫新列」，
於是 `Embedding` 一起沒了，下一步 `EmbedPendingAsync` 再全部補回來。
類別註解把全量重寫的理由寫成「數量以百計，省下哪些變了的整套判斷」——
**那個理由沒有把重嵌算進去**。匯入確實不在正式機的熱路徑上，
但它在**開發與量測的熱路徑上**，而那正是這一整個 #48 待決項待的地方。

改成逐份比對，內容一模一樣就原地不動（`SameAsIndexed`）。兩個設計要點：

- **比的是建好的列本身，不是 `ContentHash`。** 那一欄只算 `text`，
  而**核可翻轉不改動任何一個字**——拿它當依據會讓「退回一則」安靜地不生效，
  檢索照樣撈得到那一段。這比每次重嵌糟得多，也是原作者不做增量的合理擔憂。
  測試 `核可翻轉一定要重寫_即使一個字都沒改` 就是釘這一條。
- **逐欄比對的漏洞是「加了新欄位卻忘了加進比對」**，症狀同樣是沒有錯誤訊息的
  「改了 manifest 卻沒生效」。防線是 `DocChunkPersistedFieldsFacts`：
  它用反射把 `DocChunk` 的欄位清單釘死，新增欄位就紅，逼人決定它算不算「內容變了」；
  排除的五個欄位（`Id`／`IndexedAt`／嵌入三欄）每一個都要寫理由。

`ExtractImportResult` 因此拆成兩組：`EntriesTotal`／`EntriesApproved`／`EntriesStale`
回答「索引現在長什麼樣」，`EntriesRewritten`／`ManifestsUnchanged` 回答「這一輪動了什麼」
——**動了什麼決定要不要重嵌**，兩者混在一個 `EntriesWritten` 裡就分不出來。





#### 10.11.14 語意近重複掃描與語料定版（2026-08-26／27）

> 這一節補的是**執行結果**。上面〈兩件已知但沒處理的〉①只寫到「作法與為什麼併進 #48」，
> 而它已經跑完並裁決完了——逐群明細在 [`DECISIONS.md`](../../qa-extracts/DECISIONS.md)，
> 此處只留讀得懂結論所需的部分。

對 `approved:true` 的擷取物兩兩算餘弦（snowflake 向量已在庫裡）。
前 80 對（0.9911–0.8562）**併成 34 群**再送審——逐對給建議會自相矛盾，
因為同一條規則常常三則以上互相配對。使用者兩輪裁決：**退回 17 則、改寫 4 則**。

**① 最重要的結果是那 23 群判「都留」。**

群 3、6、7、8、10、16、17、18、19、20、21–25、27–29、31–34 是**同一主題底下的不同規則**，
或同一條規則刻意在兩個模組各講一次（委外報價／供應商價目表、訂單／採購、應付／應收…）。
最尖銳的是第 20 群：匯率兩則餘弦 0.8769，講的是**除法的兩個方向**，兩則都對且都該留。

> 🚨 **如果當初把這道掃描做成閘門，這 23 群會全部被誤殺。**
> §10.11 那句「不訂門檻、不做成閘門、輸出排序清單給人看」到這裡才真正被驗證是對的
> ——而它當初的理由只是「相關度門檻在 S2 就訂不出來」，並沒有預見誤殺率會高到七成。

**② 順帶抓到另一類問題：退回的 17 則裡有 6 則的毛病不是重複，是「變更歷程殘留」。**

「將**不再**透過…**現在是**依照…」「畫面權限的判斷標準**已更新**」——那是規格的修訂歷程，
不是現行規則（定案 #39 的同一類）。該類累計 8 則，**`scrub()` 與提示詞都擋不掉**。
語意掃描是**因為重複才把它們翻出來的**，這是一個沒有規劃到的副產物。

**③ 改寫不等於核可。** 上述 4 則改寫加上 `eddaa9c7`（標題「規則說明」改為具體規則），
共 5 則改完之後一直停在 `approved:false`——定案 #45 說核可是人的動作，
產生器連一個能寫出 `true` 的參數都沒有。**2026-08-27 使用者逐則過目後核可。**

語料至此**定版**：**1288 段，核可 1070、退回 218、待審 0**（manifest 250 份、過期 0）。

**④ 那次重啟順帶驗到匯入器的增量比對真的在做事，也量出了它的粒度。**

只重寫 4 份 manifest／**177 段**——而改的只有 5 則。`mfg-domain.json` 一份就佔 155 段：
**比對的單位是 manifest 不是 entry**，一份裡改一則就整份重寫，那 155 段的向量一起重算。
要讓「改一則只重嵌一則」得把比對下放到 entry 層，**目前刻意不做**：
manifest 是審核與版控的單位，拆細會讓「這份審過了」失去對應物。
代價是可預期的（改一則最壞付一份的重嵌），不是缺陷。


#### 10.11.15 G4／G5 的分診與兩臂量測（2026-08-27）

> 兩個缺口是前一天才登錄的，登錄時寫的是「先登錄、不排期」。
> 這一輪把它們各量了一次——**兩個的結論都跟登錄當下的推測不一樣**，
> 而其中一個順帶推翻了 §10.9 記著的一個數字。
> 程式在 `ManualCrossPageDiagnosisTests`／`ManualCrossLangExtractTests`（只檢索、不生成，共 30 秒）。

##### G5：不是「分數不夠」也不是「名額被同頁佔掉」，是**擷取物把名額拿走了**

登錄時我把它二分成「分數不夠（貴）」與「名額被同一頁的其他塊佔掉（便宜）」，
分診的設計就是要分辨這兩者。**兩個都不成立：**

| 量到的 | 值 |
|---|---|
| 正解頁不在候選集（深度 50）內 | **0 頁** ← 分數夠，全部找得到 |
| 正解頁在候選集內但排在第 5 名之後 | 9 頁（第 6／7／8／12／14／17／18／28／43 名） |
| 每頁只取最好的一塊之後全中的題數 | **4/12（完全不動）** ← 不是同頁重複佔格 |
| **擷取物不佔名額時全中的題數** | **8/12** ← 這才是原因 |
| 擷取物在跨頁題佔掉的名額 | **23/60 格（38%）** |

跨頁題天生要好幾格，而擷取物穩定拿走四成——**這一類是稀釋最先咬到的地方**，
因為單頁題只要正解在前 5 名就好，跨頁題少一格就是少一頁。

##### 順帶：**§10.9 那句「擷取物只讓 @1 掉 1 題」套不到預設路徑上**

那個數字量的是**純詞彙通道**（`ManualLexicalWeightSweepTests` 的等重臂）。
預設檢索模式是 semantic-first（定案 #27），而同一份 35 題在預設路徑上是：

| 直白題庫 35 題（semantic-first） | Recall@1 | Recall@5 |
|---|---|---|
| 現況（擷取物在場） | **24** | 34 |
| 擷取物不佔名額 | **33** | 34 |

**@1 掉 9 題，不是 1 題**；擷取物佔掉 70/175 格（40%）。
⚠️ 但 **@5 完全不動（34 對 34）**，而 topK 的塊會全部進 prompt——
所以對「單頁題答不答得出來」這件事，稀釋的代價目前是**零**；
真正被咬到的是①引文排序（第 1 名不再是正解）與②跨頁題（需要好幾格的那一類）。
**兩件事要分開講**，混在一起會得到「擷取物讓檢索退步 9 題」這種嚇人但不準確的結論。

> 💡 **候選修法：擷取物與手冊不共用同一個 topK 名額。**
> 擷取物平均 97 字、手冊塊平均 647 字（§10.9 已量），
> 所以「手冊 5 塊 ＋ 擷取物最多 3 段」只比現在多約 300 字 ≒ 0.2 秒 prefill，
> 卻同時解掉跨頁題與引文排序。**這是猜的，要量過才算數**——
> 上面每一列都是模擬「擷取物不佔名額」，不是模擬「另給名額」。
>
> 🚨 **上面這段話留著，因為它每一句都錯，而錯法值得記。**
> 實作並量過之後（同日，見下一節）：成本不是 0.2 秒是 **11.4 秒**、
> 跨頁題不是 8/12 是 **7/12**、引文排序（@1）**一格都沒改善**，
> 而且整個修法**輸給一個本來就存在的旋鈕**。

##### G4：開關的好處是滿的，代價量不到（但代價那一側的尺已經飽和）

`Features:QaAssistant:ExtractCrossLang`（量測當時預設 `false`＝維持原狀；**2026-08-27 使用者裁決後已翻成 `true`**，見本節末與 §17.9 G4）。
兩臂各跑一次，同一份索引、同一組權限：

| | 英文規則題 6 題<br>命中規則段落 | 英文手冊題 6 題<br>Recall@1／@5／MRR | 英文手冊題<br>擷取物佔格 | 中文 29 題<br>Recall@1／@5 | 中文規則題 6 題 |
|---|---|---|---|---|---|
| `false`（現況） | **0** | 6／6／1.000 | 0/30 | 18／28 | 6 |
| `true` | **6** | 6／6／1.000 | **3/30** | 18／28 | 6 |

**① 好處是滿的**：英文提問從一段規則都拿不到變成六題全中。
**② 中文兩列兩臂完全相同**——這是刻意設的對照組：擷取物全是 `zh`，
開關對中文在邏輯上是 no-op，**數字若動了就是這次改動有 bug 而不是效果**。
**③ 代價那一側量不出來，但要誠實說明為什麼**：英文手冊題只有 6 題且兩臂都是 6/6、MRR 都是 1.000
——**那把尺已經頂到天花板**（定案 #40 說的正是不要信這種尺）。
唯一沒飽和的證據是佔格數 **3/30（10%）**，遠低於中文的 70/145（48%），
機制上也說得通：英文提問走 `english` regconfig，對中文 `Lexemes` 切不出命中，
**擷取物只能經語意通道進來**，所以拿不到詞彙通道那一半的名額。

⚠️ **開關打開不會讓詞彙通道跟著有東西。** 語意通道不可用時（§10.7 的降級路徑）
這個開關等於沒開，而那時不會有任何錯誤訊息——與 §17.8 記過的失敗形狀同一類。

⚠️ **引文仍然是中文的 `rules/*`**：英文使用者會拿到英文答案配中文引文頁。
這是這條路便宜的原因，也是它的上限；要中文引文都消失只有走 A（產英文擷取物、重審 1070 則）。


#### 10.11.16 G5 的修法：做出來、量完、**決定不用**（2026-08-27）

> 上一節的分診指向「擷取物拿走四成名額」，於是實作了分艙
> （`Features:QaAssistant:ExtractSlots`：手冊仍取 `topK` 塊，擷取物**另外**最多 N 段）。
> **量完的結論是不要打開。**

##### 兩張表

分艙（手冊 5 塊 ＋ 擷取物另給 N 段）：

| N | 跨頁題全中 | 直白@1 | 直白@5 | 規則題 中／英 | prompt 字數 | 估算 prefill |
|---|---|---|---|---|---|---|
| **共用（現況）** | 4/12 | 24 | 34 | **6/6　6/6** | **2526** | **21.1 秒** |
| 0 | 7/12 | 33 | 34 | 0/6　0/6 | 3346 | 28.0 秒 |
| 1 | 7/12 | 24 | 34 | 5/6　5/6 | 3536 | 29.6 秒 |
| 2 | 7/12 | 24 | 34 | 5/6　5/6 | 3714 | 31.0 秒 |
| 3 | 7/12 | 24 | 34 | 6/6　5/6 | 3886 | 32.5 秒 |
| 5 | 7/12 | 24 | 34 | 6/6　6/6 | 4201 | 35.1 秒 |

共用名額、直接加大 `topK`（對照組）：

| topK | 跨頁題全中 | 直白@1 | 進 topK | 規則題 中／英 | prompt 字數 | 估算 prefill |
|---|---|---|---|---|---|---|
| 5（現況） | 4/12 | 24 | 34 | 6/6　6/6 | 2526 | 21.1 秒 |
| 6 | 5/12 | 24 | 34 | 6/6　6/6 | 2955 | 24.7 秒 |
| 7 | 6/12 | 24 | 34 | 6/6　6/6 | 3309 | 27.7 秒 |
| **8** | **7/12** | 24 | 34 | 6/6　6/6 | **3516** | **29.4 秒** |

##### 一、**分艙輸給了一個本來就存在的旋鈕**

要到同樣的跨頁題 7/12：分艙 `N=5` 要 4201 字（35.1 秒），`topK=8` 只要 3516 字（29.4 秒），
**而規則題、直白 @1、進 topK 三欄完全相同**。分艙貴 5.7 秒，還多一套程式。

**回頭看分診就知道為什麼**：漏掉的正解頁排在第 **6／7／8**／12／14／17／18／28／43 名，
**前三題就卡在名額邊界上**。所以問題從來不是「誰佔了名額」，是**「只看 5 名」**——
「擷取物擠掉手冊」是相關不是因果。分艙只是一種比較貴的看深方法，
貴在它強迫多出來的深度**只能給手冊**，而手冊塊大七倍。

##### 二、成本被我估錯一個數量級，錯法比數字重要

§10.11.15 估的是「多給 3 段約 300 字 ≒ 0.2 秒」——只算了**新增的擷取物**（97 字），
**沒算到分艙同時把原本被小塊佔住的 5 個主名額換成大塊手冊**（647 字）。
實測 2526 → 3886 字＝**+11.4 秒**。連 `N=0` 都比共用多 820 字。
**「只算增量、不算替換」是這一類估算的固定失誤形狀。**

##### 三、這張表差一點就選出 N=0，而 N=0 是把 1070 段語料整批關掉

第一版掃描沒有規則題那兩欄。照那一版看，`N=0` 在每一欄都最好（跨頁 7/12、@1 33/35、最便宜）
——因為**表上每一個指標的正解都在手冊裡**。補上規則題之後 `N=0` 立刻是 0/6　0/6。

> 🚨 這是定案 #40「只信沒飽和的指標」的另一面：**指標沒有涵蓋的東西，量尺會把它算成零成本。**
> 飽和的指標量不出進步，缺席的指標量不出退步，而後者更危險——它長得像一個全面勝出的候選。

##### 四、裁決（2026-08-27，使用者）

* **`topK` 維持 5。** 延遲是**每一則提問都付**，而跨頁題只是難題題庫 53 題裡的 12 題；
  且分診顯示跨頁題沒全中多半是**部分涵蓋**（2 頁中 1 頁），失敗形狀是「答得不完整」
  而非「答不出來」。曲線記在上表，日後真有使用者抱怨跨頁題再調——
  **性價比最好的一點是 `topK=7`**（每多一格買一題；到 8 之後下一題要再加四格）。
  ⚠️ #48 當初決定維持 `topK=5` 時**只往下量過**（5→4→3），往上這一段是這次才補的。
* **分艙程式保留，預設關閉。** 它是**量具不是功能**：掃描測試要靠這個旋鈕才重跑得出對照，
  而本專案的量測必須是可重跑的程式、不是一次性的手工紀錄（`LiveOllamaFactAttribute` 的檔頭寫過同一件事）。
  註解與 `appsettings.json` 都寫明「它輸給 topK，不要打開」。

## 十一、Phase 2：工具選取、參數解析與並發（**新增章節**）

### 11.1 工具選取：檢索優於分類路由

> ## 🔬 2026-08-21 實測更正：**「問題成立」這個前提，在目前的目錄規模下不成立**
>
> 本節原本的推論建立在「把全部工具的 JSON Schema 一次塞進 prompt 會是 5k–10k token」這個**估計值**上。
> 實測（`gemma4:latest`，經 §17.11.7 的 win profile）：
>
> | 量測 | 原假設 | 實測 |
> |---|---|---|
> | 工具數 | §六 的完整清單 | **6 支**（Phase 0-B 實際抽出的 INV 3 ＋ ORD 3） |
> | 全目錄 schema ＋ 一則提問的 prefill | 5k–10k token | **1931–1940 token** |
> | 模型能否 function-calling | 未驗證（本節假設要自己組 JSON） | ✅ **Ollama `tools` 參數原生可用**，回 `tool_calls` |
>
> **三題實測，工具選取與參數解析全對**：
>
> | 提問 | 結果 |
> |---|---|
> | 「上個月哪個客戶買最多？取前五名」 | `SalesRanking{period:LastMonth, topN:5}` ✅ |
> | 「庫存超過90天都沒有異動的品項有哪些？金額最大的前10筆」 | `SlowMovingInventory{idleDaysThreshold:90, topN:10}` ✅（**沒有誤選 `InventoryValuation`**） |
> | 「幫我查每個員工這個月的薪資明細」（目錄外） | **無 `tool_calls`**，誠實回覆沒有對應工具 ✅ §9.2 成立 |
>
> **對下表四個順位的影響**：
>
> - **順位 1（L1 權限過濾）不變，且是唯一必做的**。目錄外那題暴露了一個原本沒寫到的點：
>   模型拒答時**會把它看得到的工具全部列給使用者看**（實測列出六支的中文名）。
>   → **L1 過濾必須發生在「組 schema」的階段，不是「執行工具」的階段**；
>   放到執行階段擋，工具目錄本身就已經洩漏了。
> - **順位 3（embedding 檢索工具）現階段不需要**——1931 token 沒有膨脹問題，
>   而且它要多花一次嵌入呼叫。**順位 4（分類路由）更不需要。**
>   ⚠️ 但這是**規模函數不是結論**：§六 完整清單是 52 支報表，六支只是起點。
>   重新評估的觸發條件寫在下方。
> - **順位 2（JSON Schema 約束解碼）仍然有價值，但範圍縮小**：實測「參數填錯」確實只出現在
>   **沒有 `enum` 的欄位**——第一題回了 `sortBy:"salesAmount"`，那是模型**自己編的**值。
>   → 作法不是「全面約束解碼」，而是**把該有列舉值的欄位補上 `enum`**（`sortBy`、`groupBy`、`dateBasis`）。
>   `period` 有 18 個 `enum` 值，三題全部填對，正是反證。
>
> ### ✅ 2026-08-21 已交付：L1 過濾 ＋ schema 組裝（Phase 2 的第一個程式單元）
>
> | 檔案 | 職責 |
> |---|---|
> | `Modules/RPS/Contracts/IReportToolAccessBatch.cs` | L1 批次授權契約 |
> | `Modules/RPS/Services/ReportToolAccessBatch.cs` | **單次** DB 查詢後在記憶體判定 → **缺口 G1 解除** |
> | `Modules/QA/Contracts/QaToolSchema.cs` | schema 的序列化契約（屬性全為參考型別，避開 R5b 誤報） |
> | `Modules/QA/Services/QaToolSchemaBuilder.cs` | 反射產生 schema ＋ **在組 schema 階段**套 L1 過濾 |
>
> **三個設計決定，都有具體理由：**
>
> 1. **不改 `ReportToolCatalog`**：它是 Phase 0-B 已上線、已有守門測試的類別，
>    建構子被既有測試以 `(tools, permissionService)` 使用。代價是動詞判定被複製了一份，
>    **這個代價用 `ReportToolAccessBatchTests` 的逐支逐動詞比對買回來**——
>    漂移的症狀是沒有錯誤訊息，只是「看得到畫面的人」與「LLM 跑得動的工具」變成兩群人。
> 2. **不改 `Services/PermissionService.cs`**：它在 Core 也有，而 `core-drift-baseline.txt`
>    沒有它——改下去就是**新增一筆 Core 漂移**，而規則明令新漂移不得直接加 baseline。
>    批次能力因此落在 `Modules/RPS` 內。
> 3. **六個參數不送給模型**，每一筆都有理由，不是「先簡化」：
>    🚨 `PlantId`（`null` ＝依授權範圍，**不是查全部廠**；交給模型等於給出跨廠查詢參數）、
>    `TimeZoneId`（模型填會讓「今天」差一天）、`PageNumber`／`PageSize`（呼叫端控制）、
>    `SortBy`（**實測正是這一欄被編出 `"salesAmount"`**，而工具 `Description` 已載明預設排序）、
>    `AbsolutePeriod`（複合型別，且 `Period` 依契約優先）。
>
> 測試 32 支綠（含既有 `ReportToolCatalogTests`，證明 Phase 0-B 未被破壞）。

> ### ✅ 同日續：提問管線接上，`tool_calls` 會真的執行
>
> | 檔案 | 職責 |
> |---|---|
> | `Modules/QA/Contracts/LlmToolAnswer.cs` | `LlmToolCall`／`LlmToolAnswer`（`readonly record struct`，與 `LlmAnswer` 同形狀） |
> | `Modules/QA/Services/OllamaClient.cs` | 新增 `ILlmToolClient.ChatWithToolsAsync`（第四個介面、同一個實作） |
> | `Modules/QA/Contracts/QaToolQuery.cs` | 結果契約與 `QaToolQueryOutcome` 四種結局 |
> | `Modules/QA/Services/QaToolQueryService.cs` | §9.1 的五段流程 |
>
> **`ILlmToolClient` 為什麼不併進 `ILlmClient`**：後者的契約是「成功回傳即代表有可用的答案，
> 空字串與截斷都會拋」，而帶工具時**空 `content` ＋ 有 `tool_calls` 正是成功的樣子**——
> 沿用同一條檢查會把每一次正常的工具選取都當成失敗。
>
> **§8.7 的落點**：模型回的 `name` 是未經驗證的輸入，比對對象刻意是「送出去的那份 schema」
> 而不是完整目錄——那份已過 L1 過濾，等於一次同時擋掉「工具不存在」與「使用者沒權限」，
> 且拒絕訊息不透露該工具是否存在（否則就是用錯誤訊息回答了權限問題）。
>
> #### 🔬 live 實測（`QaToolSchemaLiveTests`，2 支）——發現了單元測試看不到的東西
>
> 程式產生的 schema 線上格式可用，三題工具全部選對（prefill 約 1580 tokens、每題 1.5–3.3 秒）。
> 但連跑兩次出現**不同結果**：
>
> | 提問 | 第一次 | 第二次 |
> |---|---|---|
> | 「上個月哪個客戶買最多？取前五名」 | `{dateBasis:"OrderDate", period:"LastMonth", topN:5}` | `{groupBy:"CustomerCode", period:"LastMonth", topN:5}` |
> | 「庫存超過90天…前10筆」 | `{idleDaysThreshold:90, period:"ThisYear"}` | `{idleDaysThreshold:90, period:"Today"}` |
>
> **兩個發現：**
>
> 1. **`temperature 0` 不保證確定性**——同一題連跑兩次，`topN` 一次有一次沒有。
>    因此 live 測試只斷言「問題裡明確出現、且對正確性必要」的參數；
>    把逐次穩定度放進斷言會讓測試變成擲骰子。
> 2. 🚨 **模型會自行填入問題中沒提到的參數**。`dateBasis` 因此**移入排除清單**：
>    `Default` 的語意是「維持該報表現行行為」，模型一填就默默改掉行為，
>    **而數字看起來完全合理、也自洽**——正是 §9.3 點名最難發現的那一類。
>    `period` **不能**排除（它是核心語意），緩解只能靠把參數回顯給使用者（§9.2-3）,
>    所以 `QaToolQueryResult` 一定帶回 `ParamsJson`。
>    ⓘ 目前風險有限：INV 三支工具**都不讀 `Period`**（`SlowMovingInventoryTool` 只讀 `AsOfDate`），
>    所以填了也是無害雜訊。**但這是現況不是保證**——哪天有支讀 `Period` 的工具進來就會踩到。
>    `AsOfDate` 保留（語意無法替代、實測未被亂填），風險同樣靠回顯承接。
>
> #### 逾時：Phase 2 讓 180 秒的語意變了
>
> 一題要**兩次**推論（選工具＋摘要），兩次各自合法用滿就是 360 秒。三層處理：
>
> | 層 | 機制 |
> |---|---|
> | 整題預算 | `Features:QaAssistant:ToolQueryBudgetSeconds`，預設 **420 秒**。**刻意大於 2×180**——預算若小於兩次單次逾時，會出現「每次都沒逾時、整題卻被砍」這種最難查的形狀（有測試釘住這個不變式） |
> | **摘要降級** | 摘要逾時／失敗 → `SummarySkipped`，**表格照樣回傳**。資料那時已經查到了，因為「話沒寫成」就丟掉等於讓使用者白等好幾分鐘。降級一定填 `Note`（§17.8） |
> | 列數上限 | 送進摘要最多 30 列，完整結果仍回畫面。§10.8.2 已量到**瓶頸在 prefill**，少送幾列比調高逾時有效 |
>
> 筆電（WSL 內 `gemma3:4b`，prefill 34 tok/s）推算：選工具約 57 秒、摘要約 30–45 秒，
> 兩次都在 180 秒內**但沒有餘裕**。→ **不要調高 `TimeoutSeconds`**（那會讓選工具階段也能耗掉幾分鐘沒人喊停），
> 要調的是 `MaxSummaryRows`。~~體感的真解是 token streaming（§11.2 C4）~~，逾時調整只是止血。
> ⚠️ **2026-08-21 更正**：串流已做（§11.2 C4），但實測顯示延遲有 93% 在 prefill 不在生成，
> 串流只搬動得了那 7%。**體感的真解是把 prompt 變小**，理由與數據見 §11.2 C4 的交付紀錄。
>
> #### 機密閘門：§十三 Phase 2「排除在 NL 目錄外」的實作（2026-08-21）
>
> 路線圖那一句是**限制不是建議**：機密等級最高的報表第一版不進 NL 目錄，只留畫面入口。
> 把畫面接上去而不做這道閘門，等於交付規格明文說不能交付的東西，所以兩者同一批做。
>
> | 決定 | 內容 |
> |---|---|
> | 判準 | `IReportToolDescriptor.ContainsMonetaryValues`——**可查證的事實，不是主觀分級**：回傳型別裡有沒有金額欄位（§八「含金額＝機密」，母體見 `report-permission-inventory.md`）。六支現況：`sales_ranking`／`inventory_valuation`／`slow_moving` 為 `true`，`order_overdue`／`return_rate`／`stock_shortage` 為 `false`（有 `[Theory]` 逐支釘住） |
> | 命名 | 刻意**不叫** `Sensitivity`。§14 定案 #2 已明定「機密不用動詞表達，分級由 `MenuPermission` 資料承載」，取那個名字會變成第二個真相來源，且很容易被當成權限用 |
> | 位置 | **組 schema 的階段**，與 L1 同一層、理由一字不差：模型拒答時會把它看得到的工具全部列給使用者（§11.1 實測），放到執行階段擋，目錄本身就已經洩漏了 |
> | 關係 | 與 L1 是 **AND 不是 OR**。開了 `AllowMonetaryTools` 仍不放行未授權的工具（有測試釘住；收斂成一個條件會在開燈那天靜默放行） |
> | 旋鈕 | `Features:QaAssistant:AllowMonetaryTools`，預設 `false`，單一組態鍵、改完重啟。**這不是權限開關**——它對有權限的人也生效，因為 §8.5 的彙總洩漏是 NL 入口獨有的風險：走畫面要一步步點，走 NL 是一句話 |
>
> #### 畫面：兩種模式共用同一個對話框（2026-08-21）
>
> S-7 加第二個端點 `AskData`，**不與 `Ask` 合併**：手冊問答讀 `DocChunks`（沒有廠別與資料級過濾），
> 資料查詢讀營運資料（現有機制全部經過）。合併就得由程式猜這一題是哪一種，
> 而猜錯的方向是**把營運資料當成手冊資料處理**，且使用者不會知道自己問到的是哪一邊。
> 所以模式由使用者明確選，**預設落在手冊問答**（有測試釘住預設值——預設落在資料查詢那一側，
> 等於每個人第一次提問都打到營運資料）。
>
> 其餘畫面決定：四種結局各有各的形狀（`NoTool`／`ToolRejected` 是正常路徑，畫成狀態不畫空表格）；
> 查詢條件回顯是**必要步驟不是裝飾**（§9.2-3）；顯示上限 200 列（與摘要的 30 列是兩回事，
> 報表工具不分頁，倒幾千列出來的外觀就是「頁面卡住了」）；表頭與每一格一律 `textContent`——
> 這一層的注入來源不是模型輸出而是**業務資料**（客戶名稱、品名、備註），§9.4 I2 的攻擊面就在這裡。
>
> 測試：單元 3611 綠（+5：機密閘門，含「未設定該鍵時」的預設值那一種）、
> 整合 +3（403／前置檢查擋在推論之前／預設模式）、UI +4（端點真的換掉／表格與回顯／
> 業務資料注入／降級與 `NoTool` 的形狀）、live 2 綠。手冊頁同步改寫（原本寫「問金額不會得到數字」，
> 接上 Phase 2 後那句話已不成立）。**尚未有多輪追問**（§11.3）。



> **重新評估的觸發條件**（避免「以後再看」）：工具數 > 20，**或** 全目錄 schema prefill > 4k token，
> 兩者任一成立就回頭做順位 3。~~以現況每支約 320 token 估，約在第 12–15 支工具時觸及。~~
>
> #### 🔬 2026-09-17 實測：兩個門檻都還沒到，但含金額那一側只剩 201 tokens 餘裕
>
> 估算值（每支 320 token）已由實測取代。量法：`QaToolSchemaBuilder` 對真實 DI 目錄組出 schema，
> 兩種開關狀態**各真的送一次最短提問**給 `gemma4:latest`，讀回 `prompt_eval_count`。
>
> | 狀態 | 工具數 | schema 字元 | prefill | 距 4k 門檻 |
> |---|---|---|---|---|
> | 現況（`AllowMonetaryTools=false`） | **8** | 8749 | **1951 tokens**（每支約 244） | 2049 |
> | 打開含金額工具（最壞情況） | **17** | 16536 | **3799 tokens** | **201** |
>
> - **工具數門檻（> 20）**：現況 17 支，未到。
> - **prefill 門檻（> 4k）**：最壞情況 3799，未到——**但只差 201 tokens**。
>   以每支約 244 token 算，**含金額工具開著時再加一支就會觸發重評**。
>
> ⚠️ **兩份都真的送一次，不外推**。2026-09-17 第一版按「每支 token 數」外推 17 支得到約 4146（越線），
> 改按字元數外推卻得到約 3688（未越線）——而這兩個數字要回答的正是「打開開關會不會跨過門檻」。
> 差異來自**含金額那九支的描述平均比較短**（最壞情況 973 字元／支，目前這八支是 1094），
> 按支數推會系統性高估。外推法自相矛盾時，唯一能結案的是把兩份都送出去。
>
> 守門：`SmartFactory.MES.IntegrationTests/QA/QaToolCatalogBudgetTests.cs`——
> 支數那一條是 always-on（不需要推論服務），prefill 那一條是 `[LiveOllamaFact]`（需 `QA_LIVE_OLLAMA=1`）。
> 兩條刻意分開：支數是純程式事實，綁在 live 測試裡等於平常沒人跑得到。

**問題成立**：若把全部工具的 JSON Schema 一次塞進 8B Q4 模型的 prompt（估計 5k–10k token），會造成 context 膨脹、參數填錯與迷航，且 KV cache 直接吃掉並發能力（§四）。

**但不採用「LLM 先分類模組、再給該類 schema」的兩階段路由**，理由：

1. 第一階段用 LLM = **兩次推論**，在單張 12 GB 卡上讓延遲翻倍，與 §11.2 的並發訴求互相矛盾。
2. **按模組分類是弱切分**：使用者不用模組思考（「上個月哪個客戶買最多」不會說 ORD），且分完仍不夠小（MFG 8 張、ORD 6 張）。
3. 跨模組提問（如「毛利」牽涉 ORD 銷售 + CST 成本）在硬分類下會落空。

**採用方案（依序）**：

| 順位 | 做法 | 說明 |
|---|---|---|
| 1 | **L1 權限預先過濾**（§8.4） | 免費且必做；一般使用者可見工具數本來就少，可能因此不需要任何路由層 |
| 2 | **JSON Schema 約束解碼** | Ollama 支援 `format` 傳入 JSON Schema 做結構化輸出。8B 模型「參數填錯」多為格式與列舉值亂編，約束解碼從根本讓它編不出非法值。**投報率高於路由** |
| 3 | **embedding 檢索工具（top-k=5）** | 對工具 `Description` 做向量檢索。Phase 1 已部署 embedding 模型與 pgvector，成本毫秒級、**不佔 8B 推論槽**、無需維護分類表、天然處理跨模組提問 |
| 4 | 分類路由 | 僅在上述三項實測後仍不足時才考慮 |

> 前置作業：先做 §六 的工具參數化盤點。目錄收斂後，此問題可能自然消失。

### 11.2 並發控制與限流

**先澄清一項既有設計**：「排程觸發不佔用推論資源」在 §二／§十三 已定義為 Phase 0 零 LLM 依賴，**不是待辦事項**。

**真正需要防護的競爭**是：**Phase 1 的手冊重建索引要跑 embedding，會打 Ollama**（§10.3）。手冊一改就重嵌，正好與互動查詢搶同一張卡。

| 規則 | 說明 |
|---|---|
| C1 | 應用層 `SemaphoreSlim` 限制並發推論數，**且必須與 Ollama 的 `OLLAMA_NUM_PARALLEL` / `OLLAMA_MAX_QUEUE` 對齊**——否則 Ollama 佇列滿回 503 時應用層還在放行 |
| C2 | **索引重建使用獨立限流並排至離峰**，優先權低於互動查詢 |
| C3 | Timeout 須有，且 `CancellationToken` 一路串到 `HttpClient` |
| C4 | **UI 做 token streaming，不做「分析中…」轉圈**。8B 本地推論首 token 約 1–2 秒、全文 10–30 秒；串流下體感可接受，純 spinner 是體感災難 |
| C5 | 每使用者速率限制（沿用專案既有速率限制機制），兼具防差分攻擊（§8.5 A2）之效 |
| C6 | 此限流**不跨實例**。專案目前無 Redis／無分散式快取，屬單機部署假設；日後 scale-out 須重新設計 |

> #### 交付紀錄（2026-08-21）：C1／C2／C5／C6 已實作；C4 於同日補上，但**它的前提被實測推翻**
>
> `InferenceGate`（singleton，`Modules/QA/Services/InferenceGate.cs`），閘門取在
> `OllamaClient.PostAsync`——那是所有推論唯一的出口，放在那裡就不會有人日後
> 新增一個方法時忘了排隊。**健康檢查刻意不經過**（它走 GET，且必須在忙碌時照樣答得出來，
> 否則 §17.8 的健康橫幅等於沒有）。
>
> | 項 | 實作 |
> |---|---|
> | C1 | `MaxConcurrentInference` 預設 **1**（CPU 推論下同一顆 CPU 跑兩題只是兩個人一起等）、`MaxQueueDepth` 預設 **4**。⚠️ **對齊 Ollama 那件事程式驗證不了**——它的 API 不吐自己的 `OLLAMA_NUM_PARALLEL`。訂得比對面大，多出來的照樣撞 503；訂小只是保守。所以預設取小的那一邊，並在 `appsettings` 註記 |
| C2 | 車道由**既有的 `EmbeddingKind` 決定，不新增參數**：`Query` ＝有人在等，`Passage` ＝建索引。這個區分本來就存在（它決定前綴），而它剛好就是「後面有沒有人在等」的界線。背景車道在互動車道有人時不進場；⚠️ **是禮讓不是保證**，讓路到拿到名額之間仍可能被插隊——不用鎖消除那個縫隙，是因為呼叫端本來就分批進來（每 `EmbedBatchSize` 一次），批次邊界就是讓路點，而為那個縫隙把兩條車道綁進同一把鎖，代價正好是背景重建會擋住互動查詢 |
| C5 | **每使用者只准一則在飛**，不可組態。⚠️ **沒有走框架的 `AddRateLimiter`**——Core `Program.cs` 的全域 `OnRejected` 會把任何限流拒絕當成登入暴力破解、發出 `LoginRateLimitExceeded` 安全告警，而那支是 Core 檔不能改。走框架等於每次 Q&A 被限流都誤報一次資安事件。改在模組內擋，順帶避開了「全域計數器被一個人開五個分頁吃光」——那個坑 Core 的速率限制註解裡就記著 |
| C6 | 未跨實例，已在類別註解與 `appsettings` 註記。單機部署假設不變 |
>
> ##### C4 串流（2026-08-21 補上）
>
> `ILlmStreamClient`（第五個介面、同一個 `OllamaClient` 實例，理由同 `ILlmToolClient`）
> ＋ `QaAssistantController.AskStream`，輸出 `application/x-ndjson`：
> `{"t":"…"}` 逐段、`{"done":{…}}` 完成、`{"error":"…"}` 失敗。
>
> | 決定 | 理由 |
> |---|---|
> | **不用 SSE／`EventSource`** | 它只能發 GET、不能帶自訂標頭，防偽權杖與取消都得另做一套。改用 `fetch` + `ReadableStream` 讀 NDJSON，權限、`ValidateAntiForgeryToken`、`RequestAborted` 取消全部沿用 `Ask` 那條路徑 |
> | **失敗一律是 200 加 `error` 訊框** | 一旦開始輸出就改不了狀態碼。分成「HTTP 錯誤」與「錯誤訊框」兩條路的話，其中一條永遠很少被走到＝很少被測到 |
> | **`done` 訊框仍回傳完整本文** | 畫面上累積的是**原始輸出**（含最後那行 `來源：`），Parse 之後的本文才是該留下的。讓伺服端給權威版本，畫面就不必複製一份「怎麼拿掉來源行」的規則 |
> | **不支援 `ReadableStream` 的瀏覽器自動走回 `/Ask`** | 舊瀏覽器上 `fetch` 會等整個回應收完，等於沒有串流又多一條路徑 |
> | **限流名額涵蓋整個列舉** | 只涵蓋「送出」的話，一題還在吐字時下一題就被放行，C1 的「同時只准一題」形同虛設且完全沒有症狀 |
>
> ⚠️ **逾時的語意跟著改變**：`HttpCompletionOption.ResponseHeadersRead` 之下
> `HttpClient.Timeout` 只涵蓋到收到回應標頭為止，之後逐行讀取不受它管。對串流而言這是想要的
> （生成久不該被砍），代價是**對面若中途卡住不吐字也不斷線就會一直等**。
> 取消仍然有效（`ct` 一路傳到 `ReadLineAsync`），沒有人按取消的情形才需要閒置偵測，**目前沒有做**。
>
> ##### ⚠️ C4 的前提被實測推翻：延遲不在生成，在 prefill
>
> 本節原文寫的是「8B 本地推論**首 token 約 1–2 秒**、全文 10–30 秒；串流下體感可接受」。
> **那個 1–2 秒在這個部署上不成立。**
>
> 2026-08-21 桌機 `gemma4:latest` CPU 推論，走完整 HTTP 路徑實測：
> **首個訊框 45.3 秒、完成 48.5 秒、共 39 個訊框**——也就是生成只佔 3.2 秒。
>
> 直接對 Ollama 量（完全繞過本專案，排除「是不是我們這邊緩衝了」）：
>
> | prompt | 首字 | 總計 | `prompt_eval_count` |
> |---|---|---|---|
> | 2 字 | **0.8 秒** | 1.4 秒 | 10 |
> | 約 3600 字 | **36.8 秒** | 39.9 秒 | 3149 |
>
> 結論：**傳輸沒有被緩衝，時間全花在讀 prompt**（CPU 上約 85 prompt tokens／秒）。
> 本功能的 prompt 是 §10.8 的 5 塊 context，光讀進去就要四十幾秒。
> **串流搬動的是那 3 秒，不是那 45 秒**——它讓答案「開始出現」的時間從 48.5 秒提前到 45.3 秒，
> 約 7%，使用者感覺不出差別。
>
> **C4 仍然保留**（實作正確、有守門、換到 GPU 或縮小 prompt 後立刻有價值），
> 但**「體感的真解是 token streaming」這句話要改**：真解是**把 prompt 變小**。
> 候選方向 ~~皆未做~~ **三個全部結案（2026-09-08）**：兩個裁決不做、一個已交付。
> 1. ~~**縮小送進模型的 context**~~ **2026-08-26 量過，裁決不做**（§10.11.16／待決 #48 第三件事，
>    `ManualContextBudgetTests`）。「省約四成」在比例上成立，**但絕對值只剩 8.1 秒**——
>    擷取物進語料後單塊平均只有 97 字，總延遲已從本節記的 48.5 秒降到約 27 秒，
>    **這個方向的理由自己縮水了一半**。5→4 看似免費（35 題 34→34），難題題庫兩個指標各掉 1 題。
>    原文留存：目前 `SelectWithinBudget` 給 5 塊、每塊上限 2000 字，降到 3 塊會直接砍掉約四成 prefill，
>    代價是召回（要用 §10.8 的題庫量，不能憑感覺調）
> 2. ~~**prefill 期間給進度**，而不是「思考中…N 秒」~~ **2026-09-08 裁決＝方案 A，已交付（C4-A）**。
>    ⚠️ 原文的判讀要留著，因為它決定了選項：這一條**不是「沒有任何提示」**——
>    送出後立刻就有「思考中…（已 N 秒）」＋取消鈕（`Index.cshtml`，2026-08-15 的 S-7 初版就有）。
>    可做的範圍有硬限制：**佔掉 25 秒的 prefill 本身，Ollama 在進行中不吐任何進度**
>    （`prompt_eval_count`／`prompt_eval_duration` 只出現在最後一個回應），
>    只能用「prompt token 數 ÷ 實測 85 tok/s」推估倒數——而那個吞吐量是**逐機器、逐模型**的常數，
>    寫死就會在別台機器上騙人。**所以裁決是報階段、不報時間。**
>
>    **交付內容**：`AskStream` 在既有的 `t`／`done`／`error` 之外多送 `{"stage":…}` 訊框——
>    `search`（**送在 `SearchAsync` 之前**，因為檢索自己要先做一次嵌入往返，
>    送在後面的話畫面在最需要回饋的那幾秒沒東西可顯示；測試釘的是「第一行」而不是「有出現」，
>    釘的就是這個順序）與 `infer`（帶 `chunks`／`chars`——「模型要讀多少」是量得到的，
>    「還要幾秒」不是）。畫面的等待標籤改成可變的，依階段換寫，秒數照舊每秒跳；
>    **收不到階段訊框也不會壞**，標籤停在原本的「思考中」，所以新舊兩端可以獨立部署。
>
>    **刻意不做的是預估秒數**（評估過的方案 B′：補接 `prompt_eval_duration`、
>    分模型保存吞吐量的滾動中位數、丟掉 KV cache 造成的離群樣本、進度條封頂 95%）。
>    理由：它**不縮短任何等待**，只把 27 秒變成「有數字的 27 秒」；而估計偏低時
>    「100% 了還在轉」比沒有進度條更傷信任。留待實際有人抱怨「不知道還要多久」再做，
>    屆時樣本已可從實際使用累積，也就不必寫死任何機器相關的常數。
>
>    ⚠️ **覆蓋的界線**：訊框順序與「零命中不送 `infer`」由 `QaAssistantScreenTests` 兩支守住
>    （已變異驗證）；`chunks`／`chars` **只有 `[LiveOllamaFact]` 驗得到**——
>    fixture 沒有向量，沒有推論服務的機器上走的是零命中短路那條路。
>    UI 測試的 stub 已補送階段訊框，但它守的是「那段前端程式碼不會炸」，
>    **不是「標籤真的顯示了階段」**：stub 一次把所有訊框送完，階段標籤只存在微秒。
>    🚨 另有一個順帶修掉的坑：`QaAssistantLiveAskTests` 原本把「不是 `t`、不是 `error`」的訊框
>    一律當成 `done`，多一種訊框就會在 `GetProperty("done")` 炸開——**而且只有真的有 Ollama 時才炸得到**
> 3. ~~換推論後端~~ **2026-08-25 使用者裁決不做**（定案 #49）。且**接了也解不掉這一項**——
>    外部 provider 改善的是生成端，而生成端只佔 7%
>
> ⚠️ **量測時務必用 `ResponseHeadersRead`**：`HttpClient` 預設會把整個回應讀完才回來，
> 那樣量到的「第一個字」永遠等於「最後一個字」，任何串流測試都會變成恆綠的裝飾。
> `QaAssistantLiveAskTests.串流時第一個字遠早於最後一個字` 因此改成斷言
> **訊框首末差 > 0.5 秒**（散佈在生成期間）而不是「首個訊框早於總時間一半」——
> 後者是把 prefill 當分母，量到的是機器快不快，不是有沒有在串流。

> **忙碌走的是既有的降級路徑**：`InferenceBusyException : OllamaException`。
> 這條繼承**是有作用的不是分類學**——摘要階段被擋 → 既有的 `catch` 直接降級成
> `SummarySkipped`（表格照樣給），選工具階段被擋 → 使用者看到一句看得懂的話。
> 有測試釘住這條繼承，斷掉的症狀是整題失敗而不是降級。
>
> 測試：`InferenceGateTests` 11 支，全部直接量狀態不量「有沒有拋例外」——
> 這一類程式最容易的壞法是「看起來有在限流」：名額少還一次會讓閘門慢慢自我鎖死，
> 多還一次會讓限流靜靜失效，兩者都沒有錯誤訊息。
>
> ⚠️ **2026-08-24：本檔的等待上限改成「有沒有卡住」的偵測器，不再是延遲斷言。**
> 取消傳遞與名額釋放都靠執行緒集區排班延續，而測試專案平行跑 3,600＋支，
> 於是 `背景等待中可被取消` 在機器忙時（dev app 在跑）穩定假紅、單獨跑必綠——
> **量到的是機器快不快，不是閘門的行為**。三處修正：上限放寬到 60 秒
> （壞掉的形狀是永遠不完成，放寬只影響「真壞時多等 30 秒」）；
> 所有等待改走 `Settled`／`AssertCanceledAsync`；**逾時的紅字要說出「這可能是機器忙」**
> ——舊寫法 `WaitAsync` 逾時拋 `TimeoutException`，而該支斷言等的是
> `OperationCanceledException`，紅字因此寫成「例外型別不符」，看起來像閘門沒聽取消。

### 11.3 多輪追問的狀態管理（✅ **2026-08-24 交付**）

> **交付紀錄（2026-08-24）**：`QaTurn` 表（`QaDbContext`／migration `AddQaTurn`）、`IQaTurnStore`／
> `QaTurnStore`、`QaToolQueryService.ConfirmAsync`、`QaAssistantController.ConfirmData`、
> S-7 的回顯確認面板。**Phase 2 的程式工作至此結束。**
>
> **⚠️ 落地時對本節做了一處實質改動，理由如下。**本節原文把追問寫成
> 「模型判斷有歧義 → 反問 → 使用者答 → 合併參數」。實作改成
> **確定性的「回顯確認」，不問模型有沒有歧義**，兩個理由都可查證：
>
> 1. **§9.3 本來就這樣寫**：「成本相關工具**一律強制回顯口徑**（不走『有歧義才問』的判斷）」。
>    讓模型自己判斷有沒有歧義，恰好是那句話禁止的做法。
> 2. **多一輪推論在 CPU 上是 45 秒起跳**（§11.2 C4 實測：時間全在 prefill，約 85 prompt tokens/秒）。
>    「反問」要多跑一次完整推論才問得出一句話，而確定性判定是零成本。
>
> 於是判定閘門只有兩條，都不必問模型：
>
> | 觸發 | 依據 |
> |---|---|
> | 工具的 `ContainsMonetaryValues == true` | §9.3。判準用既有屬性而非自訂清單——它有可查證的準則（回傳型別裡有沒有金額欄位），新工具的作者本來就被迫回答它 |
> | 模型一次選了多支工具 | §11.1 原本的行為是「取第一支並留一行 log」，使用者看不到自己問的兩件事只被答了一件 |
>
> **「合併參數」落在使用者身上而不是第二次推論**：回顯面板依 schema 把每個參數畫成控制項
> （列舉→下拉、其餘依 JSON 型別），使用者改完按確認即執行。
>
> 🚨 **合併只採用第一輪 schema 公布過的欄位**，其餘一律沿用存下來的那份。這不是防禦性寫法：
> `QaToolSchemaBuilder.Excluded` 刻意讓模型拿不到 `plantId`（跨廠）、`dateBasis`（默默改掉報表行為）、
> `sortBy`（實測被憑空編值）。確認畫面若接受任意欄位，**它就變成一個比模型還寬的參數入口**，
> 而那幾個欄位正是不該從自然語言進來的。
>
> **輪數上限的真實角色是防重放，不是 UX**：回顯面板把「選哪一支」與「參數對不對」放在同一次確認裡，
> 所以正常路徑第二輪必定執行，走不到上限。上限擋的是「拿一個已定局的 `turnId` 再送一次」。
> 本節「上限 1–2 輪」照樣成立，只是它擋下來的東西與原本設想的不同。
>
> **稽核刻意只記筆數，不存答案也不存資料列**：`QaTurns` 與 `DocChunks` 一樣沒有廠別過濾、
> 沒有資料層級過濾，摘要文字與 `Rows` 是實際的營運資料（金額、客戶名），
> 存進來等於在一個沒有防線的地方留一份副本。筆數足以回答 §5.5 T5 問的四件事
> （誰、哪支工具、什麼參數、幾筆）。
>
> **保留期修剪放在啟動時**（`DocIndexBackgroundService`，`Features:QaAssistant:TurnRetentionDays`，
> 預設 90 天），不另開每日排程：每一列都對應一次 CPU 推論，成長率有硬性上界
> （單機一天寫不到兩千列）。⚠️ 代價是**連續數月不重啟的部署不會修剪**——上界算得出來，
> 所以「沒修剪」的後果是可預期的資料量而非失控。
>
> ⚠️ **這張表與「提問不落地」的架構約束不衝突，但界線要講清楚**：§8.7 禁止的是
> 提問與答案進**向量庫**（`DocChunks`），因為那張表沒有任何權限欄位擋得住，
> 一旦寫進去，某人問題裡的單號就會出現在**別人**的檢索結果裡。`QaTurns` 沒有
> `Lexemes`／`Embedding`，不被任何檢索路徑讀取，唯一的讀取入口是 `UserId` 完全相等的
> 那個人自己的下一輪確認。

需求成立（§9.2-3 的回顯確認需要保存上一輪解析結果），**但不引進 Redis**——`Program.cs` 目前無 `AddSession`、無 `AddDistributedMemoryCache`、無 Redis，`IMemoryCache` 僅用於 `SystemParamService` / `CurrencyService` / `UomService` 三處。為追問功能拉進新的基礎設施元件，代價不成比例。

| 方案 | 評價 |
|---|---|
| **IMemoryCache + 滑動過期（10–15 分鐘）** | 最省，符合既有慣例 |
| **落表 `QaTurn`（傾向此案）** | §9.2-1 本來就要求稽核未覆蓋提問，Phase 3 又要把查詢存成 `ReportDefinition`。一張表可同時滿足追問狀態、稽核語料、Phase 3 來源；可沿用既有 `AuditLogRetentionJobService` 的保留期清理模式修剪；重啟不掉狀態 |

其他規則：

- **合併參數後必須重跑完整權限檢查**（§8.7），不可沿用第一輪授權結論。
  → 落地：`ConfirmAsync` 重新 `BuildAsync` 一次 schema（L1 過濾＋金額閘門一起重跑），
  比對後才執行。**兩輪之間角色可能被改、`AllowMonetaryTools` 可能被關掉、工具可能被下架**，
  沿用第一輪的結論等於讓這些變更作用不到已經開始的對話上。
  另加一道本節沒寫但必要的檢查：**送回的工具鍵必須是第一輪的候選之一**——
  有權限不等於「是使用者問的那件事」，權限擋不住這件事，候選清單才擋得住。
- 快取／資料表鍵必須含 user id，**永不跨使用者共用**。
  → 落地：`FindPendingAsync`／`ResolveAsync` 一律把 `UserId` 寫進 `where`，
  **不是查出來再比對**——兩者在正確的情形下等價，但只有前者能讓「忘了比對」
  變成查不到資料而不是靜默放行。「不是你的」「已經執行過」「被修剪掉」三種原因
  刻意回同一個結局，能區分它們的呼叫端也就能拿別人的 `turnId` 探測誰問過什麼。
- **限制輪數**：釐清問答上限 1–2 輪，超過即退回「請改用畫面查詢」。8B 模型在多輪長 context 下漂移嚴重，開放式對話會吃掉準確率。
  → 落地：`QaToolQueryService.MaxRounds = 2`，但它實際擋下來的是重放（見上方交付紀錄）。

## 十二、外部案例參考（Cerebras "How We Built Our Knowledge Base"）

評估後判斷：Cerebras 解決的是多來源、高噪音、企業級規模的知識庫問題，本規劃的手冊 Q&A 是單一乾淨來源的簡單問題，套用其全套架構屬過度工程化。但以下兩點具體可借鏡：

1. **混合檢索優於純語意檢索**：手冊含大量畫面名稱/欄位代號等專有名詞，建議在需求 1 的檢索層加入 PostgreSQL 全文檢索（`tsvector`）與 pgvector 語意檢索並行，以 Reciprocal Rank Fusion 融合排序，不需額外基礎設施。（已納入 §10.5）
2. **Planner/Executor/Synthesizer 三階段模式**：驗證了需求 2「NL解析→工具呼叫→摘要」設計方向的正確性，與業界模式一致。

不採用的部分：Slack式LLM噪音前處理（手冊已是乾淨文件不需要）、大規模差異化索引同步（目前資料量遠未達此規模）、MCP 原子工具介面（目前僅單一內部消費者，非必要）。

## 十五、Phase 0 導入影響評估與 Core 邊界（2026-08-03 實測）

> 本章為證據附錄，回答「進行 Phase 0 對目前系統有哪些影響」。
> 所有數字均為 2026-08-03 對 HEAD 與 `upstream/main`（tip `a51b8c5`，merge-base `a3980fa`，2026-05-19）實測。

### 15.1 影響總覽

Phase 0 的影響分三類，**風險與工作量的分佈與直覺相反**：

| 類別 | 內容 | 程式改動量 | 風險 | 主要成本 |
|---|---|---|---|---|
| **A. 立即改變線上行為** | 0-A 報表權限動詞（§8.1） | 每支 1 行 | **高** | 業務決策（61 張報表逐張裁決機密等級） |
| **B. 大面積低風險重構** | 0-B 抽 `IReportTool` | 大 | 中（**無測試網**，見 15.3） | 補特徵測試的工時 |
| **C. 新增基礎設施** | `ReportDefinition` 表＋排程＋新畫面 | 中 | 低 | 既定的登錄鏈成本（15.4） |

一句話結論：**Phase 0 最危險的不是寫新程式，而是改一個字的權限動詞；最花時間的不是重構本身，而是重構前必須先補的測試。**

### 15.2 A 類：權限動詞（詳見 §8.1）

機制、量化擴散範圍、既有暴露三項證據已寫入 §8.1，此處僅記結論性影響：

- 影響 **52 支**報表/查詢畫面（0-A0 實測後的嚴格口徑），每支僅一行 `CanCreate`/`CanRead` 之差；**其中只有 28 張需要業務裁決**。
- **此變更不留任何痕跡**：`MenuPermission` 資料列無須異動、無 migration、無稽核事件。
- 必須由業務端逐張裁決機密等級——這是**決策成本而非開發成本**，且是 Phase 0 的真實關鍵路徑。
- 附帶抓到既有暴露：`ContributionMargin`／`MarginAnalysis` 今天全 7 角色（含 OP）可見，建議獨立修復。

> **降低 0-A0 決策成本的做法**：不必把 61 張都送到業務端。先用「現行動詞 × 模組矩陣」自動分三組，
> 只有第 ② ③ 組需要人工裁決：
> ① **動詞與矩陣一致、且非金額類** → 維持現狀，不必問（預期占多數）。
> ② **`CanRead` ＋ 含金額/成本/毛利欄位** → 今天就可能過寬，**優先裁決**（`MarginAnalysis`、`ContributionMargin` 即屬此類）。
> ③ **`CanCreate` ＋ 矩陣本來就寬** → 改動詞會立即擴散，須先確認（§8.1 擴散表的四列）。
> 這能把「逐張裁決」壓到真正需要業務判斷的報表，而 Phase 0 的關鍵路徑正是這個決策。
> **2026-08-03 已依此執行完畢**：52 張母體 → 需裁決 28 張（② 13 ＋ ③ 15），其餘 22 張維持現狀、2 張是缺陷直接修。
> 明細見 [`report-permission-inventory.md`](../report-permission-inventory.md)。

### 15.3 B 類：報表重構的測試覆蓋現況

實測「測試專案中提及該報表名稱的檔案數」：

| 報表 | 測試檔提及數 |
|---|---|
| SalesRanking、OrderOverdue、ReturnRate、InventoryValuation、StockShortage、SlowMovingInventory | ~~**0**~~ → ✅ **各 4–6 條特徵測試**（2026-08-05） |
| DeliveryRate、PurchaseOverdue、ProfitLoss、BalanceSheet、APAging、WorkOrderProgress | 1 |
| GrossMargin、ARAging、DefectRate | 2（且多為服務層而非 Controller） |

`SmartFactory.MES.UITests` 共 93 個 `.cs` 檔，**0 個涵蓋報表畫面**（此項未變）。

→ 影響：把 `BuildQueryAsync` 搬成注入式 Service 時，**沒有任何測試會攔下行為改變**。
報表算錯不會 crash，只會靜默給出不同金額，而報表的消費者通常不會逐筆核對。
→ 對策已寫入 §6.1 前置作業二（特徵測試先於重構）。

**進度**：零覆蓋的那 6 支已補齊（30 條，含突變驗證）。
第二列的 6 支「提及數 1」多半只是被別的測試順帶提到，**不是針對報表輸出的斷言**——
真要動它們之前仍須比照補特徵測試，不可因為表上有個「1」就當作有網子。

### 15.4 C 類：新增基礎設施的既定成本

| 項目 | 影響 |
|---|---|
| `ReportDefinition` 新表 | ProjectZ 第 144 支 migration（現有 143 支）。須留意既有 snapshot drift 陷阱：建置要指定 csproj，否則 `dotnet ef` 產出空 migration |
| 第 8 支 `BackgroundService` | 現有 7 支（`LicenseWatchdogService`、`EtaRecalibrationBackgroundService`、`PreparationAlertBackgroundService`、`QueueTimeBackfillService`、`DailyDataAuditJobService`、`AuditLogRetentionJobService`、`PasswordHistoryRetentionJobService`）。單機假設與既有一致，**不新增架構風險**；註冊須包在 §三 功能開關內 |
| 新畫面（S-1～S-4，見 **§十六**） | 固定登錄鏈：`ProjectZMenuContributor` ＋ 2 份 resx ＋ 角色矩陣（未知群組 default-deny 會直接看不見）＋ `HelpDocResolver` 映射與手冊頁。[`HelpDocResolverTests.cs:38`](../../../SmartFactory.MES.Tests/Services/HelpDocResolverTests.cs#L38) 會斷言「已登錄畫面必須有實體手冊檔」，缺了直接紅燈。既有部署的權限由 `ComputeBackfill` 自動補，不需人工。**畫面一律依 `.claude/commands/` 規格產生，完整清單與強制項見 §十六** |
| R5b | `ReportRequestBase` 若落在 `Modules.*` 命名空間下，`DateBasis`、`bool UseLocalCurrency` 等非可空實值型別屬性會被判隱含必填，須補 `ProjectZValidationResource` 雙語鍵。`baseline-R5b.txt` 目前僅 6 行 ≒ 零容忍 |
| §5.5 T1–T5 若做成 R 系列規則 | R0 會在 build 期斷言新規則已登錄 `consistency-framework.md` 的〈規則索引〉表；現役編號至 R22 且撤案編號不回收 |

### 15.5 Core 邊界：Phase 0 需要動到 Core 的部分

#### （0）歸屬判定方法

[`scripts/check-core-drift.sh`](../../../scripts/check-core-drift.sh) 的判定規則：檔案存在於 merge-base、且 HEAD 內容 ≠ `upstream/main` tip → 算漂移；
企業新增檔（merge-base 不存在）不算漂移；`scripts/core-drift-baseline.txt` 凍結既有漂移（**現有 61 筆，只減不增**），出現新漂移即 `exit 1`。

#### （1）決定性事實：**Core 沒有任何業務模組**

```
$ git ls-tree --name-only upstream/main Modules/
Modules/.gitkeep
Modules/_TEMPLATE
```

Core Migrations 29 支、ProjectZ 143 支，兩邊各自獨立。

→ **§六 全部 61 支報表 Controller、所有 `Modules/**` 下的服務與 DTO、`RolePermissionSeeder`、`ProjectZMenuContributor`、`HelpDocResolver`、`ModuleControllerBase` 全屬 ProjectZ 專屬檔。**

→ **Phase 0 的主體——0-A 權限動詞調整、0-B 抽 `IReportTool`、`ReportDefinition` 表與 migration、排程 `BackgroundService`、新畫面——全部是零 Core 改動。**

#### （2）Phase 0 會「消費但不修改」的 Core 檔案

以下皆為 **Core 同步中**（HEAD 內容與 `upstream/main` tip 完全一致，未漂移）。只要維持唯讀消費，就不會產生任何新漂移：

| Core 檔案 | Phase 0 的用途 |
|---|---|
| [`Services/PermissionService.cs`](../../../Services/PermissionService.cs) | `IPermissionService` 的 5 個動詞（`CanRead`/`CanCreate`/`CanUpdate`/`CanDelete`/`CanPrint`）＋`CanViewData`、`IOwnedByRole` |
| [`Services/MenuService.cs`](../../../Services/MenuService.cs) | `MenuItem` / `SupportedActions` 預設值 |
| [`Services/IMenuContributor.cs`](../../../Services/IMenuContributor.cs) | `IReportToolRegistrar` 的設計原型 |
| [`Services/IAppModule.cs`](../../../Services/IAppModule.cs) | 模組註冊擴充點（排程服務由此註冊，不必改 `Program.cs`） |
| [`Services/DateTimeService.cs`](../../../Services/DateTimeService.cs) | `IDateTimeService`，`RelativePeriod` 解算的時區來源 |
| [`Services/ExportService.cs`](../../../Services/ExportService.cs) | 排程報表的 Excel 輸出 |
| [`Data/ApplicationDbContext.cs`](../../../Data/ApplicationDbContext.cs) | global query filter；三個上下文屬性 |
| [`Middleware/UserPreferenceMiddleware.cs`](../../../Middleware/UserPreferenceMiddleware.cs) | 上下文屬性的既有賦值範例 |
| [`Services/IUserContext.cs`](../../../Services/IUserContext.cs) | `ActiveRolePath` 的解析定義 |

> ⚠️ `Services/CoreMenuContributor.cs` 與 `Program.cs` **已在 baseline 內漂移中**。Phase 0 應避免再動這兩支——
> 排程服務走 `IAppModule` 在模組內註冊，不要加進 `Program.cs`，以免擴大既有漂移。

#### （3）四項「看起來需要動 Core、實際上不必」的設計選擇

這是本節重點：Phase 0 有四個地方直覺上會想改 Core，但都有 ProjectZ 側的正解。**選錯就會把一個純顧問模組的功能變成跨 repo 專案。**

**① 排程無 HTTP 上下文 → 不必改 global query filter**

直覺：排程沒有 HTTP 請求，`CurrentPlantId` 落空 → 想幫 Core 的過濾器加參數或加旁路。

實際：[`ApplicationDbContext.cs:11-13`](../../../Data/ApplicationDbContext.cs#L11-L13) 的三個上下文屬性是 **public set**，Core 自己的 `UserPreferenceMiddleware` 也只是在請求開始賦值三行。排程 scope 內做同樣三行賦值即可（詳見 §5.5 T2 補充）。**零 Core 改動，且過濾邏輯與畫面完全一致。**

**② 相對期間解算 → 不必給 `IDateTimeService` 加方法**

直覺：`RelativePeriod` 要解算成日期區間 → 想在 Core 的 `IDateTimeService` 加 `ResolvePeriod()`。

實際：現有介面 `Now` / `ToLocalTime` / `ToUtcTime` / `GetLocalDate(string? timeZoneId = null)` 已足夠，在 ProjectZ 側寫 `RelativePeriodResolver` 消費即可。

> ⚠️ **排程情境的陷阱**：[`DateTimeService.cs:64-78`](../../../Services/DateTimeService.cs#L64-L78) 的 `GetLocalDate()` 在 `timeZoneId` 為 null 時取 `_userContext.TimeZoneId`，
> 排程情境無使用者 → 靜默落到 `"Asia/Taipei"` fallback。多廠別或非台灣時區部署會**無聲算錯一天**，
> 而錯的是「上個月」的邊界，報表數字仍然自洽。→ 排程路徑**必須顯式傳入 `Plant.DefaultTimeZone`**。

**③ 報表畫面只該支援 Read/Print → 不必改 `SupportedActions` 預設值**

直覺：報表根本沒有 Create/Update/Delete，`MenuService.cs:12` 的五項預設是錯的 → 想改預設值。

實際：改 Core 預設值會**改變所有部署所有畫面**的權限計算結果，是高風險 Core 變更。正解是在 `ProjectZMenuContributor` 對報表項目逐項顯式宣告 `SupportedActions = ["Read","Print"]`（純 ProjectZ 側）。

✅ **2026-08-14 已採用**（待決 #10 裁決），52 支逐支宣告，見
[`report-permission-inventory.md` §四之三](../report-permission-inventory.md)。

> ⛔ 原本這裡寫「會造成新舊部署行為分歧、須搭配一次性資料校正腳本」——**那是錯的框法**。
> 宣告 `SupportedActions` 不改寫任何既有 `MenuPermission` 列，`SeedAsync` 只在角色尚無權限時
> 種入、步驟 3b 只補全新的選單鍵，兩者都不會回頭重算舊列，所以沒有分歧要校正。
>
> 真正的危險在**反方向**：`SupportedActions` 決定權限維護畫面畫不畫那個勾選框。
> **少宣告**一個 Controller 其實會檢查的動詞 → 勾選框消失 → 該權限既不能授予也不能撤銷，
> 而且**下次儲存任何角色時會被寫成 `false`**（靜默撤權）。
> 因此守門點是「宣告的動詞集合 ＝ Controller 實際檢查的動詞集合」的逐支比對測試
> （`ReportSupportedActionsTests`），不是校正腳本。

**④ `IReportTool` / `ReportToolCatalog` 的放置位置**

放 `Services/` 根目錄雖然屬「企業新增檔」不算漂移，但那是 Core 的目錄，日後 Core 若出現同名檔就會變成真衝突。
→ **一律放 `Modules/_Shared/`**（已是 ProjectZ 專屬目錄，`ModuleControllerBase` 亦在此）。

### 15.6 唯一可能真正需要動 Core 的項目：§8.3 legacy 放行條款

位置：[`Data/ApplicationDbContext.cs:186-203`](../../../Data/ApplicationDbContext.cs#L186-L203)（Core，目前同步中）。
`IDataAuthEntity` 的過濾條件包含「`OrganizationUnitPath` 為空 → 視為公開」的 legacy 放行，
在彙總報表上會把這批歷史資料無聲加總進金額（§8.3）。

兩條路徑：

| 路徑 | 做法 | 代價 |
|---|---|---|
| **A. 不動 Core（建議）** | ① 回填歷史資料的 `OrganizationUnitPath`；或 ② 機密類工具內顯式 `Where(e => !string.IsNullOrEmpty(e.OrganizationUnitPath))` 並在回傳中標示口徑 | 零 Core 改動、零跨部署風險、可逐張報表漸進採用 |
| **B. 動 Core** | 把 legacy 放行改為可設定（例如 `AllowLegacyNullOrgPath` 旗標，預設 `true` 維持相容） | 見下方完整流程與風險 |

若真的要走路徑 B，完整流程與風險如下：

1. **不得在 ProjectZ 就地改**。就地修改 `Data/ApplicationDbContext.cs` 會讓 `check-core-drift.sh` 直接紅燈，
   且 baseline 的政策是「只減不增」，不可為此新增一筆。
2. **先在 Core repo（`/home/dev/SmartFactory.MES`）開 PR**，合併後再 fetch/cp 同步回 ProjectZ（upstream-first）。
3. **影響面遠大於報表**：此過濾器套用在**所有** `IDataAuthEntity` 實體上，行為改變會波及每一張單據畫面、每一支既有查詢，
   不只是 Phase 0 要工具化的那 26 張報表。
4. **影響所有部署**，包含不啟用本功能的使用者——這與 §三「選用模組、預設關閉」的原則直接衝突。
5. **測試必須用真 PostgreSQL**：`ApplicationDbContext` 附近的既有註解已載明教訓——InMemory 不對 `Include` 套用過濾器，
   單元測試抓不到此類問題。
6. 同步回 ProjectZ 後需重跑整合測試與 UITests 全套，因為受影響的是全站資料可見性。

> **結論：Phase 0 應走路徑 A。** Core 變更留到有獨立、與 LLM 無關的需求時再單獨評估——
> 不該由一個「顧問模組的報表工具化」專案去改動所有部署的資料可見性語意。

### 15.7 Phase 2 才會遇到的 Core 變更：新增權限動詞

`IPermissionService` 的五個動詞定義在 Core [`Services/PermissionService.cs:7-17`](../../../Services/PermissionService.cs#L7-L17)。
若 Phase 2 決定「NL 查詢」需要一個獨立動詞（例如 `CanQuery`），代價是一條完整的跨 repo 鏈：

Core 介面 → Core 實作 → `MenuPermission` 模型新欄位 → migration → 權限維護畫面 → 種權限與 backfill 邏輯 → 所有部署。

→ §8.6(a)「用工具粒度取代欄位級權限」正是為了避開這條路：
把敏感欄位拆成獨立工具、掛獨立 MenuKey，用**既有五動詞**就能表達分級，全程留在 ProjectZ 側。
待決 #9（NL 回答算 Read 還是 Print）也應在既有動詞內解決，不要引入新動詞。

### 15.8 Core 邊界檢查清單（Phase 0 每個 PR 前）

- [ ] `git fetch upstream main && scripts/check-core-drift.sh` 無新增漂移（baseline 維持 61 筆或更少）
- [ ] 新增檔案不放在 Core 既有目錄根層（`Services/`、`Controllers/`、`Data/`、`Middleware/`），一律 `Modules/_Shared/` 或各模組目錄
- [ ] 未修改 `PermissionService.cs`、`ApplicationDbContext.cs`、`MenuService.cs`、`DateTimeService.cs`
- [ ] 排程服務經 `IAppModule` 在模組內註冊，未動 `Program.cs`（已在 baseline 漂移中，避免擴大）
- [ ] migration 只落在 ProjectZ 的 `Migrations/`

### 15.9 明確不受影響的部分

- **不動任何單據流程、過帳、簽核**——Phase 0 對業務資料只讀不寫。
- **不需要 GPU／Ollama／pgvector**（§四已述），純後端開發。
- **不影響既有 UITests 綠燈**，前提是報表重構不改 URL 與畫面結構。
- **功能開關（`Features:ReportTools:Enabled`，見 §三）關閉時連 `AddHostedService` 都不註冊**；`ReportDefinition` 空表時掃描為 no-op。
- **不影響 Core 及其他部署**（前提是採用 §15.6 路徑 A）。

## 十六、新增畫面的規範遵循（**強制，不得自創樣板**）

本規劃會新增數支畫面。**一律依 `.claude/commands/` 既有規格產生，不得自行拼裝**——
這些規格封裝的不是風格偏好，而是「漏了就會靜默壞掉」的既有教訓（權限看不見、驗證訊息不顯示、CSP 擋掉事件、手冊測試紅燈）。

### 16.1 畫面清單與適用規格

| # | 畫面 | 階段 | 適用規格 | 型式 |
|---|---|---|---|---|
| **S-1** | 報表排程維護（管理者看全部排程） | 0-B | [`/new-screen`](../../commands/new-screen.md) ＋ [`new-screen-projectz`](../../commands/new-screen-projectz.md) | 標準 CRUD（清單＋側欄表單） |
| **S-2** | 我的常用查詢（清單＋一鍵重跑） | 0-B | 同上 | CRUD；**另含既有報表畫面的「儲存此查詢」按鈕增修** |
| **S-3** | 報表收件匣／下載（§7.3 E1） | 0-B | `/new-screen` 唯讀清單型（無 Create/Update/Delete） | 清單＋下載（下載須過 §7.8 S5 權限再檢查） |
| **S-4** | 排程執行紀錄（`ReportRun`） | 0-B | `/new-screen` 唯讀清單型 | 唯讀查詢（可併為 S-1 子頁） |
| **S-5** | 報表 Excel 匯出 | 0-B | [`/xlsx`](../../commands/xlsx.md) | 既有報表 Action 增修；排程共用同一組 `ExportRequest` 組裝邏輯 |
| **S-6** | 排程健康度儀表板（可選，非必要） | 0-B 之後 | [`/new-analytics`](../../commands/new-analytics.md) | KPI 卡片＋圖表 |
| **S-7** | Q&A 對話畫面 | 1／2 | **契約不完全適用，見 §16.3** | 非 CRUD |
| **S-8** | 上述每支畫面的 UI 測試 | 隨畫面 | [`/webapp-testing`](../../commands/webapp-testing.md) | Page Object ＋ 測試類別。**S-7 與 `/QaExtract` 的已完成**（8 支，見 §16.3.2）；S-1～S-6 隨各自畫面 |

> S-5 的關鍵是**共用**：排程產生的 Excel 與畫面按鈕匯出的 Excel 必須走同一段組裝程式碼，
> 否則兩邊欄位會隨時間漂移，而沒有任何測試會發現（§15.3）。

### 16.2 共通強制項（缺一即紅燈，或更糟——靜默壞掉）

| # | 項目 | 漏掉的後果 |
|---|---|---|
| 1 | `ProjectZMenuContributor` ＋ 2 份 resx（MenuKey 對齊 Controller）＋ `RolePermissionSeeder` 角色矩陣（新群組須補 `GroupToModule`） | 未知群組 **default-deny** → 畫面做完了但**沒人看得到** |
| 2 | 手冊頁 `wwwroot/docs/user/{module}/{screen}.html` ＋ `HelpDocResolver` 登錄 | [`HelpDocResolverTests.cs:38`](../../../SmartFactory.MES.Tests/Services/HelpDocResolverTests.cs#L38) 斷言「已登錄畫面必有實體手冊檔」→ **直接紅燈** |
| 3 | 一致性框架樣板（`new-screen-projectz` §9），不得手抄 | R 系列規則 build 期擋下 |
| 4 | 事件走 `data-action` 委派，不得 inline `on*=`（同 §10） | CSP 擋掉 → 按鈕**點了沒反應且無錯誤訊息** |
| 5 | 側欄 `_Form` 每個 `asp-for` 欄位配 `asp-validation-for` span | 欄位級驗證錯誤**只閃一下不顯示**，使用者只看到存不起來 |
| 6 | offcanvas 內開 Bootstrap modal 前先搬 `document.body` | 灰屏凍結 |
| 7 | 顯示/隱藏一律 `classList` 切換，**禁用 `style.display`** | 與 `u-display-none` 衝突 → 元素永久隱藏 |
| 8 | 查詢列控件依 `CLAUDE.md`〈查詢列控件選用規範〉：報表/單據/廠商等一律 `select2-enable`，僅無法列舉者用 text | 使用者要背代碼 |
| 9 | **Save 不得直接繫結 `ReportDefinition` 實體** | `NextRunAt`／採番類伺服器產生欄位未提交 → `ModelState` 恆 invalid → **存檔靜默失敗，建不出排程**。改綁 VM，或 Save 開頭 `ModelState.Remove` |
| 10 | R5b：新實體與 DTO 的非可空實值型別屬性補 `ProjectZValidationResource` 雙語鍵 | baseline 零容忍，build 紅燈 |

**權限動詞的宣告方式（易錯）**：S-1～S-4 檢查的是**該畫面自己的 MenuKey** 五動詞（它們是使用者的個人設定，不是報表資料）；
而「執行一筆定義／點開一個常用查詢」時，另外檢查**目標報表的 MenuKey**（§7.9-4）。
兩者不可混為一談——否則會出現「有排程維護權限就能跑任何報表」的越權路徑。

### 16.3 S-7（Q&A 對話畫面）是明確例外

對話畫面沒有清單、分頁、排序、側欄表單，`/new-screen` 的 CRUD 契約套不上去。
**前例**：BAS 模組的畫面契約與 `_CrudPanelScripts` 不相容，結論是不強推（見 `.claude/` 既有紀錄）。

但下列**照樣適用，沒有例外**：

- 登錄鏈（§16.2-1）與手冊頁（§16.2-2）
- CSP／`data-action`（§16.2-4）
- i18n：不得硬編碼中文字串（Cx6／Cx7；注意舊 View 一旦 `@inject` localizer，整頁硬編碼 CJK 會一次爆成新違規）
- **LLM 輸出絕不可當 HTML 渲染**（§9.4 I2）——這是 S-7 最高風險項，須沿用既有 DOMPurify 慣例

→ S-7 動工前應先補一份「對話型畫面契約」到 `new-screen-projectz.md`，而不是在 PR 裡臨時發明。

✅ **已補（2026-08-11）**：`.claude/commands/new-screen-projectz.md` §12「對話型畫面契約」。
內容含唯一的豁免清單（§12.1）、照樣適用的七項（§12.2）、輸出渲染以
`textContent` 為預設而非「sanitize 後 innerHTML」（§12.3）、
**引文須由畫面端再過濾一次**（§12.4，幻覺引文是實測到的行為不是理論風險）、
延遲當 UX 主軸（§12.5，定案模型實測 52.8 秒/題而 `ILlmClient` 目前只有非串流 `ChatAsync`）、
兩條紅線（§12.6）與管理動作歸屬（§12.7）。**S-7 的先決條件到此解除。**

#### 16.3.1 S-7 已實作（2026-08-15）

`/QaAssistant`（`Modules/QA/{Controllers,Views}/QaAssistant*`）。契約逐條落地，
以下只記**實作時才浮出來、光看契約看不到**的五件事。

| # | 決定 | 為什麼不是另一種寫法 |
|---|---|---|
| ① | **自成選單群組 `QaManagement` → `QA_ASSISTANT = (F,N,N,N,N,R,N)`** | 群組決定權限矩陣，而助理（給人用）與擷取物一覽（給維護者看）的受眾本來就不同；併進 `AdminManagement` 等於用同一列矩陣同時決定兩件事。實跑確認 seeder 步驟 3b 只補給 HQMGR 與 MGR（Admin 走 catch-all），沒有任何作業員被自動授權 |
| ② | **`Rebuild` 的閘門是 `CanUpdate` 不是 `CanRead`** | 這是矩陣裡 Full 與 Read 的**唯一**實際差別。問問題人人可做；重建會打滿一顆 CPU 幾分鐘並與所有人的提問搶同一個推論槽（§11.2）。沒有這個區分的話，(F,…,R,…) 那一列就只是裝飾 |
| ③ | **零命中直接拒答，不送模型** | ①沒有參考資料時模型「不拒答」等於整段憑自己的知識編的，而這是可以從源頭消滅的失敗；②花 52.8 秒去拿一個必然的常數字串。⚠️ **評測刻意不這樣做**（它要量的正是空 context 時模型會不會亂答），兩邊的差異僅止於此，是知情的 |
| ④ | **`QaCitationKind` 帶 `[JsonConverter(typeof(JsonStringEnumConverter))]`** | 本專案**沒有**全域註冊字串列舉轉換器。少了這個屬性會序列化成 `0/1/2`，而畫面比對 `c.kind === 'Rule'`——數字永遠不相等，於是**每一則引文都掉進「無法查證」那一支**，把正常的手冊連結全部標成幻覺。沒有例外、沒有紅燈，只有一頁看起來像模型在亂編 |
| ⑤ | **手冊頁 `sys/qa-assistant.html` 登錄 `HelpDocResolver`；`QaExtract` 仍不登錄** | 登錄有第二個作用：本頁會被索引成 `MenuKey=QaAssistant` 的 chunk，於是「這個助理怎麼用」變成它自己答得出來的問題，而且**只答給有權限的人**。`QaExtract` 維持既有分類（使用者/角色/權限/代碼維護屬建置階段設定） |

**驗證邊界要講清楚**：`QaAssistantScreenTests`（8 綠）只涵蓋**不需要推論服務**的那一半——
權限閘門、AntiForgery、前置檢查、無 inline `on*=`、以及 §12.6 第 1 條
（提問前後 `DocChunks` 筆數不變）。真的問一題仍由 `[LiveOllamaFact]` 的評測負責。
引文分類本身是純函式（`QaCitationPresenter`，8 個單元測試），**刻意做成不需要模型也能完整驗證**——
它判錯不會拋例外、不會變紅，只會讓使用者拿到一條點不下去的連結，或一個編出來的頁碼看起來像真的。

> ✅ **互動驗證已完成**（2026-08-17，桌機經 SSH 反向通道連 Ollama，§17.11.2）：
> `QaAssistantLiveAskTests`（`[LiveOllamaFact]`）走完整條 HTTP 路徑問一題。
> 實跑「採購付款要用買入匯率還是賣出匯率」，47.4 秒，答覆正確，引文回來是
> `rules/currency-d151520a.html`、**無連結、帶展開內容**，手冊引文行為未變。
>
> 題目是選出來的：正解只在已核可的擷取物裡，手冊沒寫買入／賣出的選用時機——
> **手冊也答得出來的題目，會讓這支測試在「擷取物一則都沒進索引」時照樣通過。**
>
> 它斷言的是**原始 JSON 的字面**（`"kind":"…"`）而不是反序列化後的列舉：
> 本專案沒有全域註冊字串列舉轉換器，少了 `JsonStringEnumConverter` 就會序列化成 `0/1/2`，
> 畫面比對 `c.kind === 'Rule'` 永遠不相等，**每一則引文都會掉進「無法查證」那一支**，
> 正常的手冊連結被整批標成幻覺。用型別化 DTO 讀回來會自動把 `0` 讀成 `Manual`，
> 正好把要抓的東西抹平。

#### 16.3.2 S-8：這兩支畫面的 UI 測試（2026-08-17）

`SmartFactory.MES.UITests/QA/`，8 支全綠（7.5 秒）。
`Pages/QaAssistantPage.cs`、`Pages/QaExtractPage.cs` 是對應的 Page Object。

**這一層補的是前面三層都碰不到的那一段。** 單元測 `QaCitationPresenter` 的分類函式，
整合測權限閘門／AntiForgery／「HTML 裡沒有 inline `on*=`」，`[LiveOllamaFact]` 真的問一題——
但**那些字串到了瀏覽器裡被畫成什麼形狀**，三者都不知道。整合測試能斷言「沒有 inline handler」，
那只證明 CSP 不會擋；它證不到 `data-action-submit="qaAsk"` 真的接得上 `window.qaAsk`。
名字打錯、`@section Scripts` 沒帶 nonce、dispatcher 沒載入，三者的症狀都是
**點了沒反應、沒有錯誤訊息**，而 HTML 檢查全部照樣通過。

| # | 決定 | 為什麼不是另一種寫法 |
|---|---|---|
| ① | **`/QaAssistant/Ask` 由 Playwright `RouteAsync` 攔下來回合成 JSON，一顆模型都不需要** | 真的問一題要五十秒 CPU 推論＋Ollama＋嵌入索引，那條路已經有 `QaAssistantLiveAskTests`。搬到這一層只會讓這幾支在 CI 上被跳過而失去效力——同 `ScheduleGanttTests` 那支不依賴 DB 的合成資料測試，**能在乾淨環境上一定執行得到，才有守門的價值** |
| ② | **`[QaScreenFact]`：沒設 `QA_UI_SCREENS=1` 就 skip** | `Features:QaAssistant:Enabled` 預設關閉（§17.2），關閉時 `QAModule` 連路由都不註冊，兩個畫面是 404；而 nightly 刻意不開這個功能。沒有這道開關，這幾支會在 nightly 上以 404 常態紅，**真因是設定不是缺陷**——紅久了就沒人看，等於把整個 UI 層的訊號一起賠掉。⚠️ 與 `LiveOllamaFact` 是兩件事，不要混用：那支要推論服務，這支只要求畫面存在 |
| ③ | **XSS 那支分三個位置比對，不讀整顆氣泡** | 答案本文、`summary`、展開內容在畫面裡是三段不同的程式碼。只讀整顆氣泡的話，本文帶著 payload 就足以讓斷言成立，**摺疊區塊裡那兩段即使是空的也照樣通過**——那正是要抓的東西 |
| ④ | **展開內容用 `TextContent` 不是 `InnerText`** | `details` 收合時內容不參與排版，`InnerText` 一律回空字串，會把「內容根本沒送來」與「還沒點開」混為一談。首跑就是這樣紅的。另外補一次點開後的可見性斷言，免得展開的是一個永遠打不開的殼 |
| ⑤ | **重建索引：驗的是「按取消就不送請求」** | 重建會打滿一顆 CPU 好幾分鐘並與所有人的提問搶同一個推論槽——這正是它的閘門是 `CanUpdate` 而不是 `CanRead` 的理由。`confirm()` 誤點就送出去，那個區分就白做了。Playwright 預設會自動關掉 dialog（等同按取消），所以「不接受」那半不必額外設定 |

⚠️ **踩到兩個既有的坑，都記在測試裡了**：`form button[type='submit']` 會先命中導覽列下拉裡的
「登出」（藏在收合的 dropdown 裡，症狀是等元素可見等到逾時 30 秒，看起來像畫面壞了，
`LoginPage` 早就有同款註解）；以及引文區塊最上面那個「參考來源」標籤也帶 `mb-2`，
用 `> div.mb-2` 數引文會多算一列。

實跑輸出（`ProjectZ_E2EUI`，擷取物 472 段／已核可 265 段）：
`匯率類型選擇（rules/currency-d151520a.html）` 就地展開無連結、
`pur/nonexistent.html （模型引用了本次未提供的頁碼…）` 純文字、
`/QaExtract?sortOrder=heading_asc&state=pending&searchTerm=%E5%8C%AF%E7%8E%87&…&searched=true`
排序連結帶著篩選、總數卡片 `生效 265／未核可 207／過期 0`。


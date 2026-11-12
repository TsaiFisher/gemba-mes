// 自 home.html 內聯 <script> 外部化（CSP script-src 改用 nonce，靜態 HTML 無法帶 nonce）。
        let _pushSubscription = null;
        let _swReg = null;

        // ── 離線佇列：必須在 requireAuth() 之前 ──────────────────────────────
        // 🚨 這一段原本排在 requireAuth() 的 early return **之後**，於是「還有幾筆沒送出」
        //    在最需要它的時候看不見：離線時 requireAuth() 第一行的 /auth/me 必然失敗、
        //    回 null、主流程直接 return，佇列連初始化都沒跑到，右下角的徽章永遠不出現。
        //    使用者看到的是一頁空白，完全沒有「你的報工還在手機裡」這個訊息——
        //    那正是離線佇列存在的唯一理由。
        // 佇列是**純本地**的（IndexedDB），不需要任何認證就讀得到，所以它不該排在認證後面。
        // `online` 監聽也一起搬上來：斷網期間排入、恢復連線自動補送這條路，
        //  在「沒有重新登入過」的分頁上同樣要成立（flush 自己會處理 401，見 offline-queue.js）。
        (async () => {
            await offlineQueue.ready();
            await refreshOfflineBadge();
            document.getElementById('offlinePanel').addEventListener('show.bs.offcanvas', renderQueueList);
            window.addEventListener('online', async () => { await syncNow(); await refreshOfflineBadge(); });
        })();

        // ── 主流程：模組載入（不等 SW 就緒） ────────────────────────────────
        (async () => {
            const user = await requireAuth();
            if (!user) return;

            document.getElementById('userName').textContent = user.displayName || user.name;

            // ⚠️ 下面這兩個呼叫都**必須放在模組載入的 early return 之前**，理由是同一個：
            // `modules.length === 0` 那一行會把後面的程式碼整段跳過，而「沒有手機模組權限」
            // 的人（純辦公室的簽核者、只來簽核的主管）正是最需要這兩項資訊的一群。
            // 兩者都**刻意不 await**：它們不該擋住模組格的渲染。
            refreshPlantName();
            refreshRoleName();
            refreshApprovalBadge();

            // 模組載入：不依賴 SW，立即執行
            const data = await apiGet('/api/mobile/modules');
            if (!data) return;

            const grid    = document.getElementById('moduleGrid');
            const modules = data.modules || [];

            if (modules.length === 0) {
                document.getElementById('emptyState').classList.remove('d-none');
                return;
            }

            const colors = ['text-primary', 'text-success', 'text-warning', 'text-info', 'text-danger', 'text-secondary'];
            modules.forEach((m, i) => {
                const color = colors[i % colors.length];
                const col   = document.createElement('div');
                col.className = 'col-6 col-md-4 col-lg-3';
                const i18nKey = 'module.' + m.moduleKey;
                const label   = t(i18nKey) !== i18nKey ? t(i18nKey) : (m.displayName || m.moduleKey);
                col.innerHTML = `
                    <a href="/mobile/${m.moduleKey.toLowerCase()}/list.html"
                       class="card module-card shadow-sm p-3">
                        <i class="bi ${m.iconClass} module-icon ${color}"></i>
                        <span class="module-name">${label}</span>
                    </a>`;
                grid.appendChild(col);
            });
        })();

        // ── Web Push 初始化：獨立執行，不阻塞模組載入 ────────────────────────
        (async () => {
            if (!('serviceWorker' in navigator) || !('PushManager' in window)) return;
            try {
                // 確保 SW 已註冊（home 直接進入時補註冊）
                _swReg = await navigator.serviceWorker.register('/mobile/sw.js', { scope: '/mobile/' });
                await navigator.serviceWorker.ready;
                _pushSubscription = await _swReg.pushManager.getSubscription();
                document.getElementById('btnPush').classList.remove('u-display-none');
                updatePushIcon();
            } catch (e) {
                console.warn('[Push] SW 初始化失敗', e);
            }
        })();

        // ── 廠別 ──────────────────────────────────────────────────────────────
        let _plantInvalidPrompted = false;

        async function refreshPlantName() {
            const el = document.getElementById('plantName');
            if (!el) return;

            const data = await apiGet('/api/mobile/plants/current');
            if (!data) return;   // apiCall 已處理錯誤（含 Toast）

            // 中英名稱由伺服端一起給，這裡依 getLang() 取用（CodeEntry 慣例）。
            const zh   = getLang() === 'zh-TW';
            const name = zh ? (data.name || data.nameEn) : (data.nameEn || data.name);

            // 代碼一律顯示：現場的口語是廠別代碼（P1／P2），名稱反而不一定人人記得。
            el.textContent = data.plantId
                ? (name ? `${name} (${data.plantId})` : data.plantId)
                : (data.invalid ? t('home.plant_invalid') : t('home.plant_none'));
            el.classList.toggle('text-danger', !!data.invalid);

            // 廠別失效（管理員把它從角色移除）：直接打開切換面板。原本只顯示「未設定」，
            // 點進功能才看到「權限不足」，不知道切換廠別就能恢復（2026-11-11 筆電驗收 A4）。
            // 每次載入只開一次，關掉之後不再追著跳。
            if (data.invalid && !_plantInvalidPrompted) {
                _plantInvalidPrompted = true;
                openPlantSwitch();
            }

            // 這裡**刻意不寫回 sessionStorage.plantId**：requireAuth() 每頁進來都已經
            // 從 /api/mobile/auth/me 把它寫過一次了。同一個欄位兩個寫入者就是漂移的種子。
        }

        // ── 廠別切換 ──────────────────────────────────────────────────────────
        async function openPlantSwitch() {
            // 🚨 **先擋佇列，再開面板。** 讓人選完廠再說「不行」是最糟的順序。
            // 佇列裡的項目帶著排入當下的廠別（見 offline-queue.js），切了廠之後
            // 那些項目會在同步時被擋下——與其事後解釋，不如現在就別讓他切。
            // 看 pending 與 held（待確認，確認後還是會送）：error 的項目已經停住了，切廠不會讓它更糟。
            const pending = (await offlineQueue.getAll()).filter(r => r.status === 'pending' || r.status === 'held');
            if (pending.length > 0) {
                showToast(tf('home.plant_queue_blocked', pending.length), 'warning');
                return;
            }

            bootstrap.Offcanvas.getOrCreateInstance(document.getElementById('plantPanel')).show();
            await loadPlantList();
        }

        async function loadPlantList() {
            const list = document.getElementById('plantList');
            const hint = document.getElementById('plantHint');

            const data = await apiGet('/api/mobile/plants');
            if (!data) return;   // apiCall 已處理錯誤

            const zh     = getLang() === 'zh-TW';
            const plants = data.plants || [];

            // ⚠️ 提示語不可用「清單長度」判斷有沒有受限：不限廠的角色在只有一個廠的
            // 系統裡，清單也是一筆。roleIsRestricted 是伺服端另外給的旗標。
            hint.textContent = plants.length <= 1 && data.roleIsRestricted
                ? t('home.plant_only_one')
                : (data.roleIsRestricted ? t('home.plant_restricted') : t('home.plant_all'));

            list.textContent = '';
            plants.forEach(p => {
                const name    = zh ? (p.name || p.nameEn) : (p.nameEn || p.name);
                const current = p.id === data.currentPlantId;

                // DOM API 而非 innerHTML：廠別名稱是使用者可維護的主檔欄位。
                const row = document.createElement('button');
                row.type = 'button';
                row.className = 'btn btn-link text-decoration-none w-100 text-start '
                              + 'px-3 py-2 border-bottom d-flex align-items-center gap-2';
                row.disabled = current;

                const icon = document.createElement('i');
                icon.className = current ? 'bi bi-check-circle-fill text-success' : 'bi bi-circle text-muted';
                row.appendChild(icon);

                const label = document.createElement('span');
                label.className = current ? 'fw-semibold text-dark' : 'text-dark';
                label.textContent = name ? `${name} (${p.id})` : p.id;
                row.appendChild(label);

                if (current) {
                    const badge = document.createElement('span');
                    badge.className = 'badge bg-success ms-auto';
                    badge.textContent = t('home.plant_current');
                    row.appendChild(badge);
                } else {
                    row.dataset.plantId = p.id;
                    row.dataset.action  = 'switchPlant';
                }

                list.appendChild(row);
            });
        }

        async function switchPlant(e, el) {
            const plantId = el.dataset.plantId;
            if (!plantId) return;

            setLoading(el, true);
            const res = await apiCall('POST', '/api/mobile/plants/switch', { plantId });
            setLoading(el, false);
            if (!res) return;   // 403／錯誤都已由 apiCall 顯示
            await reloadAfterContextSwitch(res);
        }

        /** 切換廠別或角色之後：語言跟著廠走、清 SW 快取、整頁重載。兩種切換共用，規則只有一份。 */
        async function reloadAfterContextSwitch(res) {
            // 沒有個人語言的帳號跟著廠別的預設語言走（後端判定，見 MobilePlantController.Switch）
            if (res.followsPlant && res.language) localStorage.setItem('mobileLang', res.language);

            // 🚨 **一定要清快取再重載。** SW 對 /api/mobile/dispatches（5 分鐘）與
            // /api/mobile/machines（30 分鐘）是 Cache-First，而快取鍵只有 URL、
            // URL 裡沒有廠別——不清的話會繼續顯示**舊廠**的派工與機台最久 30 分鐘，
            // 畫面看起來完全正常，使用者只會覺得「切了沒用」。
            //
            // 刪**全部**而不是點名那兩個：快取名稱寫在 sw.js，在這裡再抄一份就是
            // 漂移的種子——日後有人加了第三個廠別敏感的快取，這裡沒有人會記得補。
            // 代價只是重新下載一次殼層，而這一刻必然是連線的（切換本身就是 POST）。
            try {
                const names = await caches.keys();
                await Promise.all(names.map(n => caches.delete(n)));
            } catch (err) {
                console.warn('[Plant] 清除快取失敗', err);
            }

            // 整頁重載而不是只更新標籤：各頁面的 in-page 狀態沒有統一的重置點，
            // 只換文字會留下上一個廠的清單資料。
            window.location.reload();
        }

        // ── 角色（2026-11-11 使用者要求：手機需要能切換角色）──────────────────
        // 原本只能在電腦上切；作用中角色被移除時手機更是沒有出路。規則與桌機同一份（IRoleSwitchService）。
        async function refreshRoleName() {
            const el = document.getElementById('roleName');
            if (!el) return;
            const data = await apiGet('/api/mobile/roles');
            if (!data) return;
            const zh   = getLang() === 'zh-TW';
            const role = (data.roles || []).find(r => r.id === data.currentRoleId);
            el.textContent = role ? (zh ? role.name : role.nameEn) : t('home.role_none');
            el.classList.toggle('text-danger', !role);
        }

        async function openRoleSwitch() {
            // 換角色可能連帶換廠（新角色不含目前廠別時改到預設廠），佇列規則與切換廠別相同：先擋再開。
            const pending = (await offlineQueue.getAll()).filter(r => r.status === 'pending' || r.status === 'held');
            if (pending.length > 0) {
                showToast(tf('home.role_queue_blocked', pending.length), 'warning');
                return;
            }
            bootstrap.Offcanvas.getOrCreateInstance(document.getElementById('rolePanel')).show();
            await loadRoleList();
        }

        async function loadRoleList() {
            const list = document.getElementById('roleList');
            const data = await apiGet('/api/mobile/roles');
            if (!data) return;
            const zh = getLang() === 'zh-TW';

            list.textContent = '';
            (data.roles || []).forEach(r => {
                const current = r.id === data.currentRoleId;
                // DOM API 而非 innerHTML：角色名稱是管理員可維護的主檔欄位。
                const row = document.createElement('button');
                row.type = 'button';
                row.className = 'btn btn-link text-decoration-none w-100 text-start '
                              + 'px-3 py-2 border-bottom d-flex align-items-center gap-2';
                row.disabled = current;

                const icon = document.createElement('i');
                icon.className = current ? 'bi bi-check-circle-fill text-success' : 'bi bi-circle text-muted';
                row.appendChild(icon);

                const label = document.createElement('span');
                label.className = current ? 'fw-semibold text-dark' : 'text-dark';
                label.textContent = zh ? r.name : r.nameEn;
                row.appendChild(label);

                if (current) {
                    const badge = document.createElement('span');
                    badge.className = 'badge bg-success ms-auto';
                    badge.textContent = t('home.plant_current');
                    row.appendChild(badge);
                } else {
                    row.dataset.roleId = r.id;
                    row.dataset.action = 'switchRole';
                }
                list.appendChild(row);
            });
        }

        async function switchRole(e, el) {
            const roleId = el.dataset.roleId;
            if (!roleId) return;
            setLoading(el, true);
            const res = await apiCall('POST', '/api/mobile/roles/switch', { roleId });
            setLoading(el, false);
            if (!res) return;   // 403／錯誤都已由 apiCall 顯示
            await reloadAfterContextSwitch(res);
        }

        // ── 回到前景時重抓廠別／角色 ──────────────────────────────────────────
        // 廠別與角色存在帳號上、所有裝置共用。另一台裝置切換後，手機首頁原本要手動重新整理才會更新，
        // 期間顯示的是舊廠別（2026-11-11 筆電驗收 D3）。PWA 從背景切回來、或從其他頁按返回
        // （bfcache 還原，不會重跑載入流程）時都重抓一次。
        // 也重跑 requireAuth()：它會把 sessionStorage 的 plantId 換成伺服器現值，
        // 離線佇列同步前比對「排入時的廠別」用的就是這個值；首頁本身沒有會送出的表單，換掉不會誤放行舊廠資料。
        async function refreshAccountContext() {
            if (!navigator.onLine) return;
            if (!await requireAuth()) return;
            refreshPlantName();
            refreshRoleName();
        }
        document.addEventListener('visibilitychange', () => {
            if (document.visibilityState === 'visible') refreshAccountContext();
        });
        window.addEventListener('pageshow', e => { if (e.persisted) refreshAccountContext(); });

        // ── Push UI ───────────────────────────────────────────────────────────
        function updatePushIcon() {
            const icon    = document.getElementById('pushIcon');
            const testBtn = document.getElementById('btnPushTest');
            if (_pushSubscription) {
                icon.className = 'bi bi-bell-fill';
                document.getElementById('btnPush').title = t('home.push_on');
                if (testBtn) testBtn.classList.remove('u-display-none');    // 已訂閱才顯示測試按鈕
            } else {
                icon.className = 'bi bi-bell';
                document.getElementById('btnPush').title = t('home.push_off');
                if (testBtn) testBtn.classList.add('u-display-none');
            }
        }

        async function testPush() {
            const res = await apiCall('POST', '/api/mobile/push/test', {
                title: t('home.push_test_title'),
                body:  tf('home.push_test_body', new Date().toLocaleTimeString(getLang())),
                url:   '/mobile/home.html'
            });
            if (res) showToast(res.message ?? t('home.push_test_sent'), 'success');
        }

        async function togglePush() {
            if (!_swReg) {
                showToast(t('home.push_unsupported'), 'warning');
                return;
            }
            try {
                if (_pushSubscription) {
                    await _pushSubscription.unsubscribe();
                    await apiCall('POST', '/api/mobile/push/unsubscribe', { endpoint: _pushSubscription.endpoint });
                    _pushSubscription = null;
                    updatePushIcon();
                    showToast(t('home.push_off_ok'), 'info');
                } else {
                    // ⚠️ **requestPermission() 必須是這次點擊裡的第一個 await，順序不可以換回去。**
                    // WebKit 要求權限請求落在使用者手勢的有效期內，而「先去後端拿 VAPID 公鑰」
                    // 那一個 await 就足以讓手勢失效 → iOS 上按了鈴鐺**完全沒有反應**
                    // （沒有權限對話框、沒有錯誤、沒有 Toast），Android 則因為手勢有效期較寬鬆
                    //  而正常，於是這個缺陷只會在一半的裝置上出現。
                    // 代價是「後端沒設金鑰」時使用者已經先被問過一次權限——那是可接受的順序，
                    // 反過來則是功能在 iOS 上根本不存在。
                    const permission = await Notification.requestPermission();
                    if (permission !== 'granted') { showToast(t('home.push_need_perm'), 'warning'); return; }

                    const keyData = await apiCall('GET', '/api/mobile/push/vapid-key');
                    if (!keyData?.publicKey) { showToast(t('home.push_no_key'), 'danger'); return; }

                    const key = urlBase64ToUint8Array(keyData.publicKey);
                    const sub = await _swReg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key });
                    const json = sub.toJSON();
                    await apiCall('POST', '/api/mobile/push/subscribe', {
                        endpoint:  json.endpoint,
                        p256dh:    json.keys?.p256dh,
                        auth:      json.keys?.auth,
                        userAgent: navigator.userAgent.slice(0, 200)
                    });
                    _pushSubscription = sub;
                    updatePushIcon();
                    showToast(t('home.push_on_ok'), 'success');
                }
            } catch (e) {
                console.error('[Push] 操作失敗', e);
                showToast(tf('home.push_failed', e.message || e), 'danger');
            }
        }

        function urlBase64ToUint8Array(base64String) {
            const padding = '='.repeat((4 - base64String.length % 4) % 4);
            const base64  = (base64String + padding).replace(/-/g, '+').replace(/_/g, '/');
            const raw     = atob(base64);
            return Uint8Array.from([...raw].map(c => c.charCodeAt(0)));
        }

        // ── 離線佇列管理 ─────────────────────────────────────────────────────
        // 只算／只列本人的項目：別人的待同步資料換人後也送不出去（flush 會擋），
        // 列出來只是讓下一個人看到前一個人報了什麼。見 offline-queue.js getAllMine()。
        let staleWarned = false;   // 每次進首頁提醒一次就好，同步後重新整理徽章不要一直跳
        async function refreshOfflineBadge() {
            const { mine: all, stale } = await offlineQueue.getAllMine();
            if (stale > 0 && !staleWarned) {
                staleWarned = true;
                showToast(tf('home.q_stale', stale), 'warning');
            }
            const pending = all.filter(r => r.status === 'pending').length;
            // held（排隊超過 30 分鐘、等本人確認）跟失敗一樣需要人處理，徽章用紅色
            const error   = all.filter(r => r.status === 'error' || r.status === 'held').length;
            const btn = document.getElementById('offlineBtn');
            const txt = document.getElementById('offlineBtnTxt');
            if (pending + error > 0) {
                txt.textContent = error > 0
                    ? tf('home.sync_badge_err', pending, error)
                    : tf('home.sync_badge', pending);
                btn.classList.remove('d-none');
                const fabBtn = btn.querySelector('button');
                fabBtn.className = error > 0
                    ? 'btn btn-danger shadow'
                    : 'btn btn-warning shadow';
                // CSSOM 個別屬性設值（.style.prop= 不受 CSP style-src 管制，取代被擋的 .cssText）
                fabBtn.style.borderRadius = '50px';
                fabBtn.style.fontSize = '.85rem';
                fabBtn.style.padding = '.4rem .9rem';
            } else {
                btn.classList.add('d-none');
            }
        }

        async function renderQueueList() {
            const { mine: all, others } = await offlineQueue.getAllMine();
            const el  = document.getElementById('queueList');
            const othersHtml = others > 0
                ? `<div class="text-muted small py-2">${esc(tf('home.q_others', others))}</div>`
                : '';
            if (!all.length) {
                el.innerHTML = `<div class="text-muted text-center small py-3">${t('home.queue_empty')}</div>` + othersHtml;
                return;
            }
            const STATUS = { pending: '⏳ ' + t('home.q_pending'),
                         held:    '⏸ ' + t('home.q_held'),
                         sent:    '✅ ' + t('home.q_sent'),
                         error:   '❌ ' + t('home.q_error') };
            const CLS    = { pending: 'text-warning', held: 'text-danger', sent: 'text-success', error: 'text-danger' };
            el.innerHTML = all.sort((a, b) => a.priority - b.priority || a.enqueuedAt - b.enqueuedAt)
                .map(r => `
                <div class="border-bottom py-2">
                    <div class="d-flex justify-content-between align-items-center">
                        <div class="small">
                            <span class="${CLS[r.status] ?? ''}">${STATUS[r.status] ?? r.status}</span>
                            <span class="ms-2">${esc(r.label)}</span>
                        </div>
                        <div class="d-flex gap-1">
                            ${r.status === 'error' ? `<button class="btn btn-xs btn-outline-warning u-font-size-p75rem u-padding-1px-6px" data-action="retrySingle" data-client-id="${esc(r.clientId)}">${t('home.q_retry')}</button>` : ''}
                            ${r.status === 'held' ? `<button class="btn btn-xs btn-outline-success u-font-size-p75rem u-padding-1px-6px" data-action="confirmHeld" data-client-id="${esc(r.clientId)}">${t('home.q_confirm_send')}</button>` : ''}
                            ${r.status === 'held' || r.status === 'error' ? `<button class="btn btn-xs btn-outline-secondary u-font-size-p75rem u-padding-1px-6px" data-action="discardItem" data-client-id="${esc(r.clientId)}">${t('home.q_discard')}</button>` : ''}
                        </div>
                    </div>
                    ${r.status === 'held' ? `<div class="text-danger u-font-size-p72rem">${esc(t('home.q_held_hint'))}</div>` : ''}
                    ${r.errorMsg ? `<div class="text-danger u-font-size-p72rem">${esc(r.errorMsg)}</div>` : ''}
                    ${r.warnMsg ? `<div class="text-warning u-font-size-p72rem">⚠️ ${esc(r.warnMsg)}</div>` : ''}
                    <div class="text-muted u-font-size-p7rem">${new Date(r.enqueuedAt).toLocaleString(getLang())}</div>
                </div>`).join('') + othersHtml;
        }

        // 說明與錯誤訊息來自佇列（本機可改）與伺服器回應，一律跳脫後才進 innerHTML。
        function esc(s) {
            return String(s ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;')
                .replace(/>/g, '&gt;').replace(/"/g, '&quot;');
        }

        async function syncNow() {
            const pending = await offlineQueue.getPending();
            if (!pending.length) return;
            const { sent, failed, held, busy } = await offlineQueue.flush();
            await refreshOfflineBadge();
            await renderQueueList();
            if (busy) return;   // 另一個 flush（例如 'online' 事件觸發的自動補送）正在送，結果由它回報
            if (held > 0) showToast(tf('home.sync_held', held), 'warning');
            else if (failed > 0) showToast(tf('home.sync_result', sent, failed), 'warning');
            else showToast(tf('home.sync_done', sent), 'success');
        }

        // 排隊超過 30 分鐘的項目（held）：本人看過內容後才送，或捨棄（例如已在桌機補登）。
        async function confirmHeld(e, el) {
            if (!await offlineQueue.confirmHeld(el.dataset.clientId)) return;
            await syncNow();
            await refreshOfflineBadge();
            await renderQueueList();
        }

        async function discardItem(e, el) {
            if (!confirm(t('home.q_discard_confirm'))) return;
            await offlineQueue.discard(el.dataset.clientId);
            await refreshOfflineBadge();
            await renderQueueList();
        }

        async function retryAllFailed() {
            const count = await offlineQueue.retryFailed();
            if (!count) { showToast(t('home.no_failed'), 'info'); return; }
            await syncNow();
        }

        async function retrySingle(e, el) {
            const clientId = el.dataset.clientId;
            await offlineQueue.retryOne(clientId);
            await syncNow();
        }

        async function clearSent() {
            await offlineQueue.clearSent();
            await refreshOfflineBadge();
            await renderQueueList();
            showToast(t('home.cleared'), 'info');
        }

// 待我簽核筆數。失敗就維持隱藏——這個 badge 是輔助資訊，
// 為它跳錯誤訊息會蓋掉首頁真正的內容，而清單頁自己會把載入失敗說清楚。
async function refreshApprovalBadge() {
    const el = document.getElementById('approvalCount');
    if (!el) return;
    const data = await apiCall('GET', '/api/mobile/approval/count');
    if (!data || !data.count) return;
    el.textContent = data.count;
    el.classList.remove('d-none');
}

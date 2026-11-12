# mobile-pwa 附錄：未採用的 SPA（Vue 3 + Pinia + Workbox）參考程式碼

> ⚠️ **未採用方案，實作請勿據此建立 Vue 工程**。定案為原生 JS + Cookie（見 mobile-pwa.md 頂部架構決策、§9 Phase E1、§11.4）。
> 本檔自 `mobile-pwa.md` §13 抽出封存，保留供「日後若出現長離線需求需升級 SPA」時參考。
> 仍適用的框架無關概念（原生 JS 版照樣要做）：應用層離線佇列（IndexedDB）+ `online` 事件主動同步 + 後端 `clientId`/`clientTimestamp` 去重；Service Worker 只做讀取快取、不重放寫入。

---

## 13. 程式碼層慣例（Vue 3 + Pinia + Workbox）

> ⚠️ **整節為未採用的 SPA 替代方案，實作勿用**。定案為原生 JS + Cookie（見頂部架構決策、§9 Phase E1、§11.4）。
> 本節的 `SmartFactory.MES.Mobile/`（Vite/Vue/Pinia/Axios-JWT）目錄結構、元件、Store、攔截器、Workbox 寫入重放等**皆不建立**。
> 仍適用的概念（與框架無關、原生 JS 版照樣要做）：**應用層離線佇列（IndexedDB）+ `online` 事件主動同步 + 後端 `clientId`/`clientTimestamp` 去重**；Service Worker 只做讀取快取、不重放寫入。

Phase E 開發前的實作基準，涵蓋專案結構、元件設計、狀態管理、離線機制。

### 13.1 專案目錄結構

```
SmartFactory.MES.Mobile/          # SPA 前端專案根
├── vite.config.ts
├── tsconfig.json
├── package.json
├── index.html
├── public/
│   ├── manifest.json             # PWA 安裝資訊
│   ├── icons/                    # 各尺寸圖示（192x192, 512x512 等）
│   └── robots.txt
├── src/
│   ├── main.ts
│   ├── App.vue                   # 根元件
│   ├── api/
│   │   ├── client.ts             # Axios + JWT 攔截器
│   │   ├── auth.ts               # 認證 API（login, refresh）
│   │   ├── dispatches.ts         # 派工 API
│   │   ├── sessions.ts           # 上工 API
│   │   ├── reports.ts            # 報工 API
│   │   ├── machines.ts           # 機台 API
│   │   └── types.ts              # API Response 類型定義
│   ├── stores/
│   │   ├── auth.ts               # 認證狀態 (Pinia)
│   │   ├── offline.ts            # 離線佇列狀態
│   │   └── master.ts             # 主檔快取（派工、產品、機台）
│   ├── views/
│   │   ├── LoginView.vue
│   │   ├── DispatchListView.vue
│   │   ├── SetupFormView.vue
│   │   ├── SessionStartView.vue
│   │   ├── ReportFormView.vue
│   │   ├── MachineStatusView.vue
│   │   └── ScheduleView.vue
│   ├── components/
│   │   ├── BarcodeScanner.vue
│   │   ├── OfflineIndicator.vue
│   │   ├── SyncStatus.vue
│   │   └── LocalForms.vue        # 待同步表單清單
│   ├── services/
│   │   ├── tokenService.ts       # Token 儲存（sessionStorage + IndexedDB）
│   │   ├── offlineQueue.ts       # 離線佇列操作邏輯（獨立於 Store）
│   │   ├── indexedDbService.ts   # IndexedDB 封裝
│   │   └── barcodeService.ts     # 條碼掃描邏輯
│   ├── types/
│   │   ├── models.ts             # Entity 介面定義
│   │   └── api.ts                # API 請求 / 回應型別
│   ├── utils/
│   │   ├── datetime.ts           # 日期時間工具
│   │   ├── uomConverter.ts       # UOM 轉換
│   │   └── formatter.ts          # 格式化（金額、數字）
│   ├── i18n/
│   │   ├── index.ts              # i18n 設定
│   │   ├── zh-TW.json
│   │   └── en-US.json
│   └── sw.ts                     # Service Worker（Workbox 包裝）
├── tests/
│   ├── unit/
│   │   ├── stores/
│   │   └── services/
│   └── e2e/                      # Playwright
└── dist/                         # 編譯輸出（指向 MES wwwroot/mobile/）
```

### 13.2 Vue 3 元件設計規範

#### 組合式 API（Composition API） + Setup Script

```vue
<template>
  <div class="dispatch-list">
    <div v-if="loading" class="spinner">加載中...</div>
    <div v-else-if="!dispatches.length" class="empty-state">
      無派工
    </div>
    <div v-else>
      <div v-for="d in dispatches" :key="d.id" class="dispatch-card">
        <h5>{{ d.dispatchNo }}</h5>
        <p>{{ d.productName }}</p>
        <button @click="startSetup(d.id)">開始準備</button>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRoute } from 'vue-router'
import { useDispatchStore } from '@/stores/dispatch'
import { useAuthStore } from '@/stores/auth'
import type { Dispatch } from '@/types/models'
import * as dispatchAPI from '@/api/dispatches'

// Props / Emits
interface Props {
  machineId?: string
}
const props = withDefaults(defineProps<Props>(), {
  machineId: undefined
})
const emit = defineEmits<{
  setupStart: [id: string]
}>()

// 狀態
const loading = ref(false)
const dispatches = ref<Dispatch[]>([])
const authStore = useAuthStore()
const dispatchStore = useDispatchStore()

// 計算屬性
const filteredDispatches = computed(() => {
  if (props.machineId) {
    return dispatches.value.filter(d => d.machineId === props.machineId)
  }
  return dispatches.value
})

// 生命週期
onMounted(async () => {
  await loadDispatches()
})

// 方法
async function loadDispatches() {
  loading.value = true
  try {
    const data = await dispatchAPI.getDispatches({
      status: 'Scheduled',
      machineId: props.machineId,
      plantId: authStore.currentPlant?.id
    })
    dispatches.value = data
  } catch (e) {
    console.error('載入派工失敗', e)
  } finally {
    loading.value = false
  }
}

function startSetup(dispatchId: string) {
  emit('setupStart', dispatchId)
}
</script>

<style scoped>
.dispatch-list { /* ... */ }
.dispatch-card { /* ... */ }
</style>
```

**重點**：
- 使用 `<script setup>` 簡化 Composition API 寫法
- Props / Emits 顯式型別定義
- 邏輯與模板分離，易於測試
- 避免 $ref / $computed（太新，瀏覽器支援不足）

#### 列表載入（可分頁）

```vue
<script setup lang="ts">
import { ref, onMounted } from 'vue'
import * as dispatchAPI from '@/api/dispatches'

const dispatches = ref([])
const page = ref(1)
const pageSize = ref(20)
const total = ref(0)
const loading = ref(false)

onMounted(async () => {
  await loadPage(1)
})

async function loadPage(pageNum: number) {
  loading.value = true
  try {
    const result = await dispatchAPI.getDispatchesPaged({
      page: pageNum,
      pageSize: pageSize.value
    })
    dispatches.value = result.items
    total.value = result.total
    page.value = pageNum
  } finally {
    loading.value = false
  }
}

function nextPage() {
  if (page.value * pageSize.value < total.value) {
    loadPage(page.value + 1)
  }
}
</script>
```

### 13.3 Pinia 狀態管理

#### 認證 Store（token 持久化）

```typescript
// stores/auth.ts
import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { useTokenService } from '@/services/tokenService'
import * as authAPI from '@/api/auth'

export const useAuthStore = defineStore('auth', () => {
  const tokenService = useTokenService()
  
  const accessToken = ref<string | null>(null)
  const user = ref<{ id: string; name: string; roles: string[] } | null>(null)
  const isLoggedIn = computed(() => !!accessToken.value && !!user.value)

  // 初始化（從 sessionStorage + IndexedDB 復原）
  async function init() {
    const saved = tokenService.getAccessToken()
    if (saved) {
      accessToken.value = saved
      // 驗證 token（可選）
      // 若過期，嘗試 refresh
      const refreshed = await refreshIfNeeded()
      if (!refreshed) {
        logout()
      }
    }
  }

  // 登入
  async function login(username: string, password: string) {
    const resp = await authAPI.login({ username, password })
    accessToken.value = resp.accessToken
    user.value = resp.user
    
    await tokenService.saveTokens(resp.accessToken, resp.refreshToken)
  }

  // Refresh Token（JWT 過期時自動調用）
  async function refreshIfNeeded() {
    const refreshToken = await tokenService.getRefreshToken()
    if (!refreshToken) return false
    
    try {
      const resp = await authAPI.refresh({ refreshToken })
      accessToken.value = resp.accessToken
      await tokenService.saveTokens(resp.accessToken, resp.refreshToken)
      return true
    } catch {
      return false
    }
  }

  // 登出
  function logout() {
    accessToken.value = null
    user.value = null
    tokenService.clearTokens()
  }

  return {
    accessToken,
    user,
    isLoggedIn,
    init,
    login,
    refreshIfNeeded,
    logout
  }
}, {
  persist: false  // ⚠️ Pinia-persist 不用於 token，改用 tokenService 手管
})
```

#### 離線佇列 Store

```typescript
// stores/offline.ts
import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { useIndexedDbService } from '@/services/indexedDbService'

export interface QueuedRequest {
  id: string              // clientId
  timestamp: number       // clientTimestamp
  endpoint: string        // '/api/mobile/reports'
  method: 'POST' | 'PATCH'
  payload: any
  createdAt: number
  status: 'pending' | 'sent' | 'error'
  errorMessage?: string
}

export const useOfflineStore = defineStore('offline', () => {
  const db = useIndexedDbService()
  
  const queue = ref<QueuedRequest[]>([])
  const isSyncing = ref(false)

  // 初始化：載入 IndexedDB 的待同步隊列
  async function init() {
    queue.value = await db.getPendingRequests()
  }

  // 加入隊列
  async function enqueue(
    endpoint: string,
    method: 'POST' | 'PATCH',
    payload: any,
    clientId: string,
    clientTimestamp: number
  ) {
    const req: QueuedRequest = {
      id: clientId,
      timestamp: clientTimestamp,
      endpoint,
      method,
      payload,
      createdAt: Date.now(),
      status: 'pending'
    }
    queue.value.push(req)
    await db.saveQueuedRequest(req)
  }

  // 同步（由 Service Worker 或主執行緒呼叫）
  async function sync(apiClient: any) {
    if (isSyncing.value) return
    isSyncing.value = true

    try {
      const toSync = queue.value.filter(r => r.status === 'pending')
      
      for (const req of toSync) {
        try {
          const response = await apiClient.request({
            method: req.method,
            url: req.endpoint,
            data: req.payload
          })
          
          req.status = 'sent'
          await db.markRequestSent(req.id)
          queue.value = queue.value.filter(r => r.id !== req.id)
        } catch (e) {
          req.status = 'error'
          req.errorMessage = e instanceof Error ? e.message : '未知錯誤'
          await db.saveQueuedRequest(req)
        }
      }
    } finally {
      isSyncing.value = false
    }
  }

  return { queue, isSyncing, init, enqueue, sync }
})
```

### 13.4 API 客戶端與 JWT 攔截器

```typescript
// api/client.ts
import axios, { AxiosInstance } from 'axios'
import { useAuthStore } from '@/stores/auth'
import { useOfflineStore } from '@/stores/offline'

export function createApiClient(): AxiosInstance {
  const client = axios.create({
    baseURL: '/api/mobile',
    timeout: 10000
  })

  // 請求攔截器：加入 JWT
  client.interceptors.request.use((config) => {
    const authStore = useAuthStore()
    if (authStore.accessToken) {
      config.headers.Authorization = `Bearer ${authStore.accessToken}`
    }
    return config
  })

  // 回應攔截器：自動 refresh + 離線容錯
  client.interceptors.response.use(
    (response) => response,
    async (error) => {
      const authStore = useAuthStore()
      const offlineStore = useOfflineStore()
      
      // JWT 過期 → 自動 refresh
      if (error.response?.status === 401) {
        const refreshed = await authStore.refreshIfNeeded()
        if (refreshed) {
          // 重新嘗試原請求
          return client.request(error.config)
        } else {
          authStore.logout()
          throw error
        }
      }

      // 網路離線 → 放進佇列（POST/PATCH only）
      if (!navigator.onLine && ['POST', 'PATCH'].includes(error.config.method?.toUpperCase())) {
        await offlineStore.enqueue(
          error.config.url,
          error.config.method.toUpperCase(),
          error.config.data,
          error.config.headers['X-Client-Id'],
          error.config.headers['X-Client-Timestamp']
        )
        // 假回應，告訴 UI 已排隊
        return {
          status: 202,
          data: { success: true, queued: true }
        }
      }

      throw error
    }
  )

  return client
}

export const apiClient = createApiClient()
```

### 13.5 Service Worker + Workbox 離線策略

**重要**：Service Worker 僅負責**靜態資源快取** + **讀取 API 快取**，**不做寫入重放**。
寫入佇列完全由應用層（Pinia Store）管理，見 § 5.2.2。

```typescript
// src/sw.ts
import { precacheAndRoute } from 'workbox-precaching'
import { registerRoute } from 'workbox-routing'
import { CacheFirst, NetworkFirst, StaleWhileRevalidate } from 'workbox-strategies'
import { ExpirationPlugin } from 'workbox-expiration'

declare const self: ServiceWorkerGlobalScope

// 預快取（靜態資源 + 編譯時清單）
precacheAndRoute(self.__WB_MANIFEST)

// 路由：登入（Network First，登入狀態很重要）
// ⚠️ 登入必須連線，不快取，避免 Token 竄用
registerRoute(
  ({ url }) => url.pathname.includes('/api/mobile/auth/'),
  new NetworkFirst({
    cacheName: 'auth-cache',
    networkTimeoutSeconds: 5
  })
)

// 路由：派工列表（Cache First，離線可瀏覽）
registerRoute(
  ({ url }) => url.pathname.includes('/api/mobile/dispatches'),
  new CacheFirst({
    cacheName: 'dispatches-cache',
    plugins: [
      new ExpirationPlugin({
        maxEntries: 50,
        maxAgeSeconds: 5 * 60  // 5 分鐘過期，強制更新
      })
    ]
  })
)

// 路由：產品、機台等主檔（Cache First + 定期更新）
registerRoute(
  ({ url }) => url.pathname.includes('/api/mobile/master/'),
  new CacheFirst({
    cacheName: 'master-cache',
    plugins: [
      new ExpirationPlugin({
        maxEntries: 100,
        maxAgeSeconds: 30 * 60  // 30 分鐘過期
      })
    ]
  })
)

// ⚠️ 報工 / 機台狀態 / 上工等寫入 API：**不在 Service Worker 層處理**
// 所有寫入由應用層 Pinia offline queue + 主程式同步完成
// Service Worker 層不做 Background Sync（避免 JWT 失效問題）

// Service Worker 更新通知
self.addEventListener('controllerchange', () => {
  // 通知頁面有新版本可用
  self.clients.matchAll().then(clients => {
    clients.forEach(client => {
      client.postMessage({
        type: 'SW_UPDATE_AVAILABLE',
        version: VITE_APP_VERSION  // 環境變數注入
      })
    })
  })
})
```

**離線寫入流程對應**：
- Service Worker 快取讀取 API（派工、產品、機台等）
- 主程式捕獲網路恢復 → 觸發 offlineQueue.sync()
- offlineQueue 先 refresh token → 再依序發送 API
- API 層接收 clientId + clientTimestamp 進行去重與幂等處理

---


using Xunit;

// 整合測試共用同一個 PostgreSQL 測試庫（SmartFactoryMES_Test）。
// 不同 xUnit collection（"Integration" 與 "ScheduleIntegration"）預設會平行執行，
// 其各自的 Seed() 會插入相同的主檔（Plant / MenuPermission 等），平行時互撞唯一鍵（23505）。
// 停用跨 collection 平行，讓兩個 collection 序列執行以隔離共用庫的種子資料。
// （單一 collection 內的測試本就序列執行；本設定僅序列化 collection 之間。）
[assembly: CollectionBehavior(DisableTestParallelization = true)]

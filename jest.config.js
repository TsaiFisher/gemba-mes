/** @type {import('jest').Config} */
module.exports = {
    testEnvironment: 'jsdom',
    testEnvironmentOptions: {
        url: 'http://localhost/',
    },
    // 手機端（wwwroot/mobile/js）長期是零覆蓋——UITests 一支都沒碰 /mobile，
    // 而離線佇列的失效方式全都是安靜的，人工測試抽樣抽不到。
    testMatch: [
        '**/wwwroot/js/__tests__/**/*.test.js',
        '**/wwwroot/mobile/js/__tests__/**/*.test.js',
    ],
    collectCoverageFrom: ['wwwroot/js/*.js', 'wwwroot/mobile/js/*.js'],
    // 忽略 .NET 建置產物（bin 內含 playwright-core 等 package.json，會與 haste-map 撞名）
    modulePathIgnorePatterns: ['/bin/', '/obj/'],
    testPathIgnorePatterns: ['/node_modules/', '/bin/', '/obj/'],
};

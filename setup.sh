#!/usr/bin/env bash
# ============================================================
# setup.sh — SmartFactory.MES 顧問環境初始化
#
# 執行環境：WSL2（Windows Subsystem for Linux）
#
# 執行方式：
#   bash setup.sh
#
# 流程：
#   1. 確認 .NET SDK
#   2. 產生環境指紋 → 申請 License
#   3. 確認 license.json + Security DLL
#   4. 設定資料庫連線字串
#   5. 啟動 PostgreSQL（Docker）
# ============================================================
set -e

echo "============================================================"
echo "  SmartFactory.MES 顧問環境初始化"
echo "============================================================"
echo ""

# ── 確認 WSL 環境 ────────────────────────────────────────────
if ! grep -qi "microsoft" /proc/version 2>/dev/null; then
    echo "  ⚠️  偵測到非 WSL 環境，此腳本專為 WSL2 設計。"
    echo "  若您確定要在此環境繼續，請按 Enter；否則請按 Ctrl+C 中止。"
    read -r
fi

# ── [1/5] 確認 .NET SDK ──────────────────────────────────────
echo "── [1/5] 確認 .NET SDK ─────────────────────────────────"
if ! command -v dotnet &>/dev/null; then
    echo "  ❌ 找不到 dotnet 指令。"
    echo ""
    echo "  請在 WSL2 中安裝 .NET 10 SDK（Ubuntu 24.04 官方套件庫即有）："
    echo "    sudo apt-get update && sudo apt-get install -y dotnet-sdk-10.0"
    echo ""
    echo "  或參閱：https://learn.microsoft.com/dotnet/core/install/linux"
    exit 1
fi
DOTNET_VER=$(dotnet --version)
echo "  ✅ .NET SDK ${DOTNET_VER}"
echo ""

# ── [2/5] 產生環境指紋 ───────────────────────────────────────
echo "── [2/5] 產生環境指紋 ──────────────────────────────────"
echo ""
echo "  License 綁定此設備的硬體指紋。"
echo "  請執行以下指令取得指紋，然後傳送給 SmartFactory 申請授權套件："
echo ""
echo "    bash tools/get-fingerprint.sh"
echo ""
bash tools/get-fingerprint.sh
echo ""

# ── [3/5] 確認 License + Security DLL ───────────────────────
echo "── [3/5] 確認授權檔案（license.json + Security DLL）───"
echo ""

LICENSE_OK=true
DLL_OK=true

if [ ! -f "license.json" ]; then
    LICENSE_OK=false
    echo "  ❌ 找不到 license.json"
    echo ""
    echo "  請將 SmartFactory 提供的 license.json 放至此目錄後，重新執行 setup.sh"
    echo ""
    echo "  申請方式："
    echo "    1. 將上方指紋（SHA256 值）傳送至 license@smartfactory.tw"
    echo "    2. 主旨：[License 申請] 顧問姓名 / 公司"
    echo "    3. SmartFactory 將回傳 license.json 及 Security DLL"
    echo ""
fi

if [ ! -f "Security/SmartFactory.MES.Security.dll" ]; then
    DLL_OK=false
    echo "  ❌ 找不到 Security/SmartFactory.MES.Security.dll"
    echo ""
    echo "  此 DLL 為 SmartFactory 授權套件的一部分，請確認："
    echo "    - 已收到 SmartFactory 提供的完整授權套件"
    echo "    - 將 DLL 放至 Security/ 目錄下"
    echo ""
fi

if [ "$LICENSE_OK" = "false" ] || [ "$DLL_OK" = "false" ]; then
    echo "  請取得上述檔案後重新執行 setup.sh"
    exit 1
fi

echo "  ✅ license.json"
echo "  ✅ Security/SmartFactory.MES.Security.dll"
echo ""

# ── [4/5] 設定資料庫連線字串 ─────────────────────────────────
echo "── [4/5] 設定資料庫連線字串 ────────────────────────────"
APPSETTINGS="appsettings.Development.json"

if grep -q "CHANGE_ME" "${APPSETTINGS}" 2>/dev/null; then
    echo ""
    echo "  請選擇 PostgreSQL 來源："
    echo "    1) 使用 Docker Desktop（自動啟動，推薦）"
    echo "    2) 已有本機 PostgreSQL（在 WSL 內自行安裝）"
    echo "    3) 遠端 PostgreSQL"
    echo ""
    read -p "  請輸入選項 [1]: " PG_CHOICE
    PG_CHOICE="${PG_CHOICE:-1}"

    case "$PG_CHOICE" in
      1)
        DB_HOST="localhost"
        DB_NAME="SmartFactoryMES"
        DB_USER="dev"
        read -p "  DB 密碼 [Dev12345]: " DB_PASS; DB_PASS="${DB_PASS:-Dev12345}"
        ;;
      2|3)
        echo ""
        read -p "  Host     [localhost]: " DB_HOST;   DB_HOST="${DB_HOST:-localhost}"
        read -p "  Port     [5432]: "      DB_PORT;   DB_PORT="${DB_PORT:-5432}"
        read -p "  Database [SmartFactoryMES]: " DB_NAME; DB_NAME="${DB_NAME:-SmartFactoryMES}"
        read -p "  Username [dev]: "       DB_USER;   DB_USER="${DB_USER:-dev}"
        read -p "  Password: "             DB_PASS
        if [ -z "$DB_PASS" ]; then
          echo "  ❌ 密碼不可為空"; exit 1
        fi
        ;;
      *)
        echo "  ❌ 無效選項"; exit 1
        ;;
    esac

    sed -i "s|Host=localhost;Database=SmartFactoryMES;Username=dev;Password=CHANGE_ME|Host=${DB_HOST};Database=${DB_NAME};Username=${DB_USER};Password=${DB_PASS}|" "${APPSETTINGS}"
    echo ""
    echo "  ✅ appsettings.Development.json 已更新"
else
    PG_CHOICE="2"
    echo "  ✅ 連線字串已設定，略過"
fi
echo ""

# ── [5/5] 啟動 PostgreSQL ────────────────────────────────────
echo "── [5/5] 啟動 PostgreSQL ───────────────────────────────"
if [ "${PG_CHOICE}" = "1" ]; then
    if command -v docker &>/dev/null; then
        echo "  啟動 Docker 容器..."
        DB_PASSWORD="${DB_PASS}" docker compose up -d db
        echo "  等待資料庫就緒..."
        for i in $(seq 1 15); do
            if docker compose exec -T db pg_isready -U dev -q 2>/dev/null; then
                echo "  ✅ PostgreSQL 已就緒"
                break
            fi
            sleep 2
            if [ $i -eq 15 ]; then
                echo "  ⚠️  資料庫等待超時，請確認 Docker Desktop 是否已啟動並啟用 WSL2 整合"
                echo "      Docker Desktop → Settings → Resources → WSL Integration → 啟用您的 WSL distro"
            fi
        done
    else
        echo "  ❌ 找不到 Docker。請安裝 Docker Desktop for Windows："
        echo ""
        echo "    https://www.docker.com/products/docker-desktop/"
        echo ""
        echo "  安裝後請確認啟用 WSL2 整合："
        echo "    Docker Desktop → Settings → Resources → WSL Integration"
        echo "    → 勾選您使用的 WSL distro → Apply & Restart"
        echo ""
        echo "  安裝完成後重新開啟 WSL 終端機再執行此腳本。"
        echo ""
        echo "  或改用本機 PostgreSQL（重新執行 setup.sh 選擇選項 2）"
        exit 1
    fi
else
    echo "  （使用外部 PostgreSQL，略過啟動步驟）"
fi
echo ""

# ── 完成 ─────────────────────────────────────────────────────
echo "============================================================"
echo "  設定完成！"
echo ""
echo "  資料庫 Migration 將於首次啟動時自動套用。"
echo ""
echo "  啟動應用程式："
echo "    dotnet run --urls \"http://localhost:5110;https://localhost:7054\""
echo ""
echo "  若遇到問題請參閱 CONSULTANT-ONBOARDING.md"
echo "============================================================"

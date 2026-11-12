# 多階段建置。建置內容（context）＝ repo 根目錄；.dockerignore 排除私鑰、授權檔、DLL 與測試專案。
# 授權（SECURITY-OPERATIONS.md §18.15、CONSULTANT-ONBOARDING.md 步驟 P4）：
#   - 環境指紋來自機器錨定檔：放在 volume（/var/lib/smartfactory），容器重建不變；不再看 hostname。
#   - 授權確認結果（lease.json）也寫在同一個 volume——預設寫在 license.json 旁（/app），容器重建就得重新確認。
#   - 容器一律視為「容器」部署：授權每 90 天由管理員確認一次（瀏覽器代為確認，系統不連外）。
#   - license.json 與 Security DLL 執行期唯讀掛載（DLL 只讀執行目錄的 license.json，不看 License:Path）。
#   - 映像附帶 MachineId（取申請碼）與 EncryptConnectionString（在容器內加密連線字串）兩支工具。

# Stage 1: 建置
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 先複製專案檔還原相依套件（利用快取層）
COPY SmartFactory.MES.csproj ./
COPY SmartFactory.MES.MachineId/SmartFactory.MES.MachineId.csproj SmartFactory.MES.MachineId/
COPY tools/EncryptConnectionString/EncryptConnectionString.csproj tools/EncryptConnectionString/
RUN dotnet restore SmartFactory.MES.csproj \
 && dotnet restore SmartFactory.MES.MachineId/SmartFactory.MES.MachineId.csproj \
 && dotnet restore tools/EncryptConnectionString/EncryptConnectionString.csproj

COPY . .
RUN dotnet publish SmartFactory.MES.csproj -c Release -o /app/publish --no-restore \
 && dotnet publish SmartFactory.MES.MachineId/SmartFactory.MES.MachineId.csproj -c Release -o /app/publish/tools/MachineId --no-restore \
 && dotnet publish tools/EncryptConnectionString/EncryptConnectionString.csproj -c Release -o /app/publish/tools/EncryptConnectionString --no-restore

# Stage 2: 執行環境
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# 非 root 使用者執行（資安最佳實踐）。錨定檔目錄與 /app/keys（部署專案以 DATAPROTECTION_KEYS_DIR 指向、掛 volume）
# 先建好並交給 appuser：具名 volume 第一次掛載時沿用映像內目錄的擁有者，否則 appuser 寫不進去
# .NET 10 的映像改以 Ubuntu 24.04 為基底（.NET 8 是 Debian 12）：沒有 adduser，且 UID 1000 已被內建的 ubuntu 使用者佔用。
# 先移除 ubuntu 再以 UID/GID 1000 建 appuser——與 .NET 8 映像的 appuser 相同，既有 volume 裡的錨定檔／lease.json 擁有者對得上。
RUN userdel --remove ubuntu 2>/dev/null; useradd --uid 1000 --create-home --user-group appuser \
 && mkdir -p /var/lib/smartfactory /app/keys \
 && chown -R appuser /app /var/lib/smartfactory
COPY --from=build --chown=appuser /app/publish .
USER appuser

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    SMARTFACTORY_ANCHOR_PATH=/var/lib/smartfactory/instance-id \
    License__LeasePath=/var/lib/smartfactory/lease.json
VOLUME ["/var/lib/smartfactory"]

# 工具用法（在 docker-compose.yml 所在目錄；工具要蓋掉 ENTRYPOINT 才不會跑成主程式）：
#   docker compose run --rm --no-deps --entrypoint dotnet app tools/MachineId/SmartFactory.MES.MachineId.dll --init   # 首次：產生錨定碼
#   docker compose run --rm --no-deps --entrypoint dotnet app tools/MachineId/SmartFactory.MES.MachineId.dll          # 申請碼與登記網址
#   docker compose run --rm --no-deps --entrypoint dotnet app tools/EncryptConnectionString/EncryptConnectionString.dll \
#       --license-id <licenseId> --plaintext "Host=db;Port=5432;Database=...;Username=...;Password=..." --no-host-binding --verify
ENTRYPOINT ["dotnet", "SmartFactory.MES.dll"]

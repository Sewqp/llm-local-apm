# dashboard(React)를 빌드해 CollectionServer의 wwwroot로 넣고, 단일 실행 파일(exe)로 퍼블리시한다.
# 결과물: publish\CollectionServer.exe 하나만 실행하면 API + 대시보드가 같은 포트에서 뜬다.
$ErrorAction = "Stop"
$root = $PSScriptRoot

Write-Host "[1/3] 대시보드 빌드 중..."
Push-Location "$root\dashboard"
npm install
npm run build
Pop-Location

Write-Host "[2/3] wwwroot로 복사 중..."
$wwwroot = "$root\src\CollectionServer\wwwroot"
Remove-Item $wwwroot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $wwwroot -Force | Out-Null
Copy-Item "$root\dashboard\dist\*" -Destination $wwwroot -Recurse -Force

Write-Host "[3/3] CollectionServer 단일 exe로 퍼블리시 중..."
dotnet publish "$root\src\CollectionServer\CollectionServer.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o "$root\publish"

Write-Host "완료: $root\publish\CollectionServer.exe"

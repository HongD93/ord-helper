param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

foreach ($project in @('source/ord-unit-probe/OrdUnitProbe.csproj', 'source/ord-unit-probe/desktop-host/OrdHelperDesktop.csproj')) {
    & dotnet build (Join-Path $root $project) -c Release --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "빌드 실패: $project" }
}

$web = Join-Path $root 'source/ord-unit-probe/wwwroot'
$scripts = @(Get-ChildItem -LiteralPath $web -File -Filter '*.js')
foreach ($script in $scripts) {
    & node --check $script.FullName
    if ($LASTEXITCODE -ne 0) { throw "JavaScript 문법 오류: $($script.Name)" }
}

$htmlCheck = 'const fs=require("node:fs"),vm=require("node:vm"),html=fs.readFileSync(process.argv[1],"utf8");let count=0;for(const match of html.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/g)){new vm.Script(match[1]);count++;}if(!count)throw Error("No inline scripts found");'
& node -e $htmlCheck (Join-Path $web 'index.html')
if ($LASTEXITCODE -ne 0) { throw 'HTML 스크립트 문법 오류' }

$regressionOutput = & node (Join-Path $root 'analysis/tests/nightmare-strategy-portable-regression.test.cjs') | Out-String
if ($LASTEXITCODE -ne 0) { throw "추천 회귀검사 실패`n$regressionOutput" }
$regression = $regressionOutput | ConvertFrom-Json
if ($regression.passed -ne $regression.total -or $regression.total -lt 1) { throw '추천 회귀검사 결과 불일치' }

[pscustomobject]@{
    status = 'passed'
    projectBuilds = 2
    javaScriptSyntaxFiles = $scripts.Count
    htmlInlineSyntax = 'passed'
    syntheticRegressionChecks = $regression.passed
    runtimeDataRequired = $false
    gameInputSent = $false
} | ConvertTo-Json

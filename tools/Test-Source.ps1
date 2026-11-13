param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

foreach ($project in @('source/ord-unit-probe/OrdUnitProbe.csproj', 'source/ord-unit-probe/desktop-host/OrdHelperDesktop.csproj')) {
    & dotnet build (Join-Path $root $project) -c Release --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "빌드 실패: $project" }
}

$automaticClearOutput = & dotnet (Join-Path $root 'source/ord-unit-probe/bin/Release/net10.0/OrdUnitProbe.dll') --self-test-local-clear-recommendations | Out-String
if ($LASTEXITCODE -ne 0) { throw "자동 클리어 추천 저장 검사 실패`n$automaticClearOutput" }

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

& node (Join-Path $root 'analysis/tests/map-version-recommendation-regression.test.cjs')
if ($LASTEXITCODE -ne 0) { throw '맵 버전 변경 시 목록 분류 및 첫 조합 추천 검사 실패' }

$localClearOutput = & node (Join-Path $root 'source/ord-unit-probe/recommendation/local-native-clear-import.test.js') | Out-String
if ($LASTEXITCODE -ne 0) { throw "로컬 클리어 회귀검사 실패`n$localClearOutput" }
$localClear = $localClearOutput | ConvertFrom-Json
if ($localClear.passed -ne $localClear.total -or $localClear.total -lt 1) { throw '로컬 클리어 회귀검사 결과 불일치' }

$randomModuleChecks = 0
foreach ($testName in @('random-combat-reference', 'random-support-adaptation')) {
    $moduleOutput = & node (Join-Path $root ('source/ord-unit-probe/recommendation/' + $testName + '.test.js')) | Out-String
    if ($LASTEXITCODE -ne 0) { throw "$testName 검사 실패`n$moduleOutput" }
    $moduleResult = $moduleOutput | ConvertFrom-Json
    if ($moduleResult.passed -ne $moduleResult.total -or $moduleResult.total -lt 1) { throw "$testName 결과 불일치" }
    $randomModuleChecks += $moduleResult.passed
}

$rosterOutput = & node (Join-Path $root 'source/ord-unit-probe/recommendation/partial-roster-guard.test.js') | Out-String
if ($LASTEXITCODE -ne 0) { throw "부분 명단 검사 실패`n$rosterOutput" }
$rosterResult = $rosterOutput | ConvertFrom-Json
if ($rosterResult.passed -ne $rosterResult.total -or $rosterResult.total -lt 1) { throw '부분 명단 검사 결과 불일치' }

$localMergeOutput = & node (Join-Path $root 'source/ord-unit-probe/recommendation/local-clear-live-merge.test.js') | Out-String
if ($LASTEXITCODE -ne 0) { throw "자동 클리어 추천 연결 검사 실패`n$localMergeOutput" }
$localMerge = $localMergeOutput | ConvertFrom-Json
if ($localMerge.passed -ne $localMerge.total -or $localMerge.total -lt 1) { throw '자동 클리어 추천 연결 결과 불일치' }

[pscustomobject]@{
    status = 'passed'
    projectBuilds = 2
    automaticClearNativeChecks = 'passed'
    javaScriptSyntaxFiles = $scripts.Count
    htmlInlineSyntax = 'passed'
    syntheticRegressionChecks = $regression.passed
    mapVersionRecommendationChecks = 'passed'
    localClearRegressionChecks = $localClear.passed
    randomUnitRegressionChecks = $randomModuleChecks
    partialRosterRegressionChecks = $rosterResult.passed
    automaticClearMergeChecks = $localMerge.passed
    runtimeDataRequired = $false
    gameInputSent = $false
} | ConvertTo-Json

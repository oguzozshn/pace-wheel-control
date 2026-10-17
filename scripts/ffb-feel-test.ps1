<#
.SYNOPSIS
    Guided force feedback test for Phase 2: checks whether gain and autocenter persist after the
    process that set them releases the wheel, and which test effects the wheel actually plays.

.DESCRIPTION
    Each step runs pacewheel in a separate process, then asks how it felt. Answers are appended
    to the results file so they can be written up in docs/device-notes.md.
    Every effect is capped at 20% strength and 5 s; Esc or Ctrl+C stops it immediately.
    -Language tr shows the step texts in Turkish; pacewheel's own output stays in English.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\ffb-feel-test.ps1 -ResultsPath ffb-results.txt -Language tr
#>
param(
    [string]$ResultsPath = "ffb-feel-test-results.txt",
    [ValidateSet('en', 'tr')]
    [string]$Language = 'en',
    # 'full' runs every step; 'gain' only the constant-force gain persistence check.
    [ValidateSet('full', 'gain')]
    [string]$Suite = 'full'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\PaceWheel.Cli\bin\Debug\net8.0-windows10.0.19041.0\pacewheel.exe'
if (-not (Test-Path $exe)) {
    Write-Host "Build first: dotnet build" -ForegroundColor Red
    exit 1
}

# Picks the text for the selected language.
function T([string]$en, [string]$tr) {
    if ($Language -eq 'tr') { $tr } else { $en }
}

function Write-Result([string]$line) {
    Add-Content -Path $ResultsPath -Value $line -Encoding UTF8
}

function Invoke-Step {
    param(
        [string]$Id,
        [string]$Title,
        [string]$Instructions,
        [string[][]]$Commands,
        [string]$Question
    )
    Write-Host ""
    Write-Host "==== $(T 'Step' 'Adım') $Id : $Title" -ForegroundColor Cyan
    Write-Host $Instructions
    Read-Host (T 'Press Enter when you are holding the wheel and ready' 'Direksiyonu tutuyor ve hazırsan Enter''a bas') | Out-Null
    foreach ($arguments in $Commands) {
        Write-Host "> pacewheel $($arguments -join ' ')" -ForegroundColor DarkGray
        # Echo pacewheel's output and keep its objective measurement in the results file.
        & $exe @arguments | ForEach-Object {
            Write-Host $_
            if ($_ -like 'MEASURED:*') { Write-Result "[$Id] $($arguments -join ' ') :: $_" }
        }
        Write-Result "[$Id] pacewheel $($arguments -join ' ') -> exit $LASTEXITCODE"
    }
    $answer = Read-Host $Question
    Write-Result "[$Id] $Title :: $Question :: $answer"
}

Write-Result "# FFB feel test $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ($Language, suite $Suite)"

if ($Suite -eq 'gain') {
    # Constant force at 10% / 2 s overcomes the firmware centering spring; 5% / 1 s is not felt.
    # Each step pushes right, then left, and pacewheel logs the measured steering displacement,
    # so the result doesn't depend on how strong it felt.
    $right = @('test', 'constant', '--strength', '10', '--seconds', '2')
    $left = $right + '--left'
    $touch = T 'Rest your hands very lightly on the wheel, the same way in every step; do not grip it. It pushes right, then left.' `
               'Ellerini her adımda aynı şekilde, direksiyona çok hafifçe değdir; kavrama. Önce sağa, sonra sola itecek.'
    $note = T 'Note anything odd (or just press Enter)' 'Garip bir şey olduysa yaz (yoksa sadece Enter)'

    Invoke-Step -Id 'G1' `
        -Title (T 'Constant 10% right and left, 2 s each, gain untouched (reference)' 'Sabit kuvvet %10 sağa ve sola, 2''şer saniye, kazanca dokunulmadan (referans)') `
        -Instructions $touch `
        -Commands @($right, $left) `
        -Question $note

    Invoke-Step -Id 'G2' `
        -Title (T 'Same, with gain 30% in the same process' 'Aynısı, aynı süreçte kazanç %30') `
        -Instructions $touch `
        -Commands @(($right + '--gain', '30'), ($left + '--gain', '30')) `
        -Question $note

    Invoke-Step -Id 'G3' `
        -Title (T 'Gain 30% set by a process that exits, then G1 in new processes' 'Kazancı %30 yapan süreç kapanıyor, sonra yeni süreçlerde G1') `
        -Instructions $touch `
        -Commands @(@('gain', '30'), $right, $left) `
        -Question $note

    Write-Host ""
    Write-Host "$(T 'All steps done. Results:' 'Tüm adımlar bitti. Sonuçlar:') $ResultsPath" -ForegroundColor Green
    exit 0
}

Invoke-Step -Id '0' `
    -Title (T 'Idle baseline (no app holds the wheel)' 'Boşta referans (hiçbir uygulama direksiyonu tutmuyor)') `
    -Instructions (T 'Nothing is running. Turn the wheel about 90 degrees by hand and let go.' `
                     'Hiçbir şey çalışmıyor. Direksiyonu elle yaklaşık 90 derece çevir ve bırak.') `
    -Commands @() `
    -Question (T 'Does it pull back to center by itself? Answer y/n and a strength 0-10' `
                 'Kendi kendine merkeze dönüyor mu? e/h ve güç 0-10')

Invoke-Step -Id '1' `
    -Title (T 'Sine, device gain untouched (reference)' 'Sinüs, cihaz kazancına dokunulmadan (referans)') `
    -Instructions (T 'A gentle 5 Hz wobble at 20% strength for 3 s. Remember how strong it feels.' `
                     '%20 güçte, 3 saniye boyunca hafif 5 Hz titreşim. Ne kadar güçlü hissettirdiğini aklında tut.') `
    -Commands @(, @('test', 'sine', '--strength', '20', '--seconds', '3')) `
    -Question (T 'Did you feel it? Strength 0-10 (this is the reference)' `
                 'Hissettin mi? Güç 0-10 (bu referans)')

Invoke-Step -Id '2' `
    -Title (T 'Sine with gain 30% set in the same process' 'Aynı süreçte kazanç %30 yapılarak sinüs') `
    -Instructions (T 'Same effect, but the process sets device gain to 30% first.' `
                     'Aynı efekt, ama önce cihaz kazancı %30''a ayarlanıyor.') `
    -Commands @(, @('test', 'sine', '--strength', '20', '--seconds', '3', '--gain', '30')) `
    -Question (T 'Strength 0-10 compared to step 1 (weaker = gain works)' `
                 'Adım 1''e göre güç 0-10 (daha zayıfsa kazanç çalışıyor)')

Invoke-Step -Id '3' `
    -Title (T 'Gain 30% set by a process that exits, then sine in a new process' `
              'Kazancı %30 yapan süreç kapanıyor, sonra yeni süreçte sinüs') `
    -Instructions (T 'First "gain 30" runs and exits. Then a new process plays the step 1 effect without touching gain.' `
                     'Önce "gain 30" çalışıp kapanıyor. Sonra yeni bir süreç kazanca dokunmadan adım 1''deki efekti çalıyor.') `
    -Commands @(@('gain', '30'), @('test', 'sine', '--strength', '20', '--seconds', '3')) `
    -Question (T 'Strength 0-10 compared to step 1 (same as step 1 = gain did NOT persist)' `
                 'Adım 1''e göre güç 0-10 (adım 1 ile aynıysa kazanç KALICI DEĞİL)')

Invoke-Step -Id '4' `
    -Title (T 'Autocenter off, held for 8 s' 'Otomatik ortalama kapalı, 8 saniye tutuluyor') `
    -Instructions (T 'While it is held, turn the wheel by hand and let go.' `
                     'Bu sürede direksiyonu elle çevir ve bırak.') `
    -Commands @(, @('autocenter', 'off', '--hold', '8')) `
    -Question (T 'While held: did centering go away (wheel limp)? y/n and centering strength 0-10' `
                 'Tutulurken ortalama kayboldu mu (direksiyon gevşek)? e/h ve ortalama gücü 0-10')

Invoke-Step -Id '5' `
    -Title (T 'After the autocenter-off process exited' 'Ortalamayı kapatan süreç kapandıktan sonra') `
    -Instructions (T 'Nothing is running now. Turn the wheel by hand and let go.' `
                     'Şu an hiçbir şey çalışmıyor. Direksiyonu elle çevir ve bırak.') `
    -Commands @() `
    -Question (T 'Is centering back like step 0? y/n and strength 0-10 (back = autocenter did NOT persist)' `
                 'Ortalama adım 0''daki gibi geri geldi mi? e/h ve güç 0-10 (geldiyse ayar KALICI DEĞİL)')

Invoke-Step -Id '6' `
    -Title (T 'Autocenter on, held for 8 s' 'Otomatik ortalama açık, 8 saniye tutuluyor') `
    -Instructions (T 'While it is held, turn the wheel by hand and let go.' `
                     'Bu sürede direksiyonu elle çevir ve bırak.') `
    -Commands @(, @('autocenter', 'on', '--hold', '8')) `
    -Question (T 'While held: centering strength 0-10, compared to step 0' `
                 'Tutulurken ortalama gücü 0-10, adım 0''a göre')

Invoke-Step -Id '7' `
    -Title (T 'Constant force, 10% to the right for 2 s' 'Sabit kuvvet, %10 sağa, 2 saniye') `
    -Instructions (T 'The wheel should push to the right.' 'Direksiyon sağa doğru itmeli.') `
    -Commands @(, @('test', 'constant', '--strength', '10')) `
    -Question (T 'Did it push right? y/n and strength 0-10' 'Sağa itti mi? e/h ve güç 0-10')

Invoke-Step -Id '8' `
    -Title (T 'Spring, 15% for 5 s' 'Yay, %15, 5 saniye') `
    -Instructions (T 'Turn the wheel away from center while it plays; it should pull back toward center.' `
                     'Efekt çalarken direksiyonu merkezden uzağa çevir; merkeze doğru geri çekmeli.') `
    -Commands @(, @('test', 'spring', '--strength', '15', '--seconds', '5')) `
    -Question (T 'Did it pull toward center more than at idle? y/n and strength 0-10' `
                 'Boştakinden daha fazla merkeze çekti mi? e/h ve güç 0-10')

Invoke-Step -Id '9' `
    -Title (T 'Damper, 15% for 5 s' 'Sönümleyici (damper), %15, 5 saniye') `
    -Instructions (T 'Turn the wheel quickly back and forth while it plays; it should feel heavier the faster you turn.' `
                     'Efekt çalarken direksiyonu hızlıca sağa sola çevir; ne kadar hızlı çevirirsen o kadar ağır gelmeli.') `
    -Commands @(, @('test', 'damper', '--strength', '15', '--seconds', '5')) `
    -Question (T 'Did it feel heavier/viscous? y/n and strength 0-10' `
                 'Daha ağır / yağlı gibi hissettirdi mi? e/h ve güç 0-10')

Write-Host ""
Write-Host "$(T 'All steps done. Results:' 'Tüm adımlar bitti. Sonuçlar:') $ResultsPath" -ForegroundColor Green

# ps1_script.ps1 — PowerShell 语法综合脚本
# 验证 PS 兼容语法的端到端执行。
$numbers = @(1, 2, 3, 4, 5)
$sum = 0
foreach ($n in $numbers) {
    $sum = $sum + $n
}

function Get-Double($x) {
    return $x * 2
}

$doubled = Get-Double 21
$sum

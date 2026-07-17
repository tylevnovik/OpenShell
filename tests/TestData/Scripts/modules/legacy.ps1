# legacy.ps1 — PowerShell 兼容模块
# 使用 PS 语法定义函数，供 .ps1 文件加载测试使用。
function Add-WithTax($price, $taxRate) {
    return $price + ($price * $taxRate)
}

function Get-Greeting($name) {
    return "Hello, " + $name + "!"
}

$PI = 3.14159265358979

# 清理 Spike C 测试期间安装的 Titanium 根证书（仅今天 2036/8/19 到期的测试证书，
# 保留旧项目 2026/7/15 创建的那份），并确认系统代理未被改动。
foreach ($store in 'My', 'Root') {
    Get-ChildItem "Cert:\CurrentUser\$store" | Where-Object {
        $_.Subject -match 'Titanium' -and $_.NotAfter.ToString('yyyy/M/d') -eq '2036/8/19'
    } | ForEach-Object {
        Write-Output "removing from $store : $($_.Thumbprint)"
        Remove-Item "Cert:\CurrentUser\$store\$($_.Thumbprint)" -ErrorAction SilentlyContinue
    }
}
$proxy = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
Write-Output "ProxyEnable=$($proxy.ProxyEnable) ProxyServer=$($proxy.ProxyServer)"

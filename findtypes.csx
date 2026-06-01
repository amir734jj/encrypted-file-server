using System;
using System.Linq;
using System.Reflection;

var asm = Assembly.LoadFrom("Api/bin/Debug/net10.0/FubarDev.FtpServer.Abstractions.dll");
foreach (var t in asm.GetExportedTypes().Where(t => 
    t.Name.Contains("Auth", StringComparison.OrdinalIgnoreCase) || 
    t.Name.Contains("Cert", StringComparison.OrdinalIgnoreCase) ||
    t.Name.Contains("Tls", StringComparison.OrdinalIgnoreCase) ||
    t.Name.Contains("Ssl", StringComparison.OrdinalIgnoreCase) ||
    t.Name.Contains("Secure", StringComparison.OrdinalIgnoreCase)))
    Console.WriteLine(t.FullName);

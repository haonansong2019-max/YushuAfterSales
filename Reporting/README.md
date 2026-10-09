# Report engine

`YushuAfterSales.Reporting` produces the local diagnostic package used by the Codex support workflow. It has no network client and does not execute SFC, DISM, installers, or target programs.

```csharp
ReportDocument report = ReportBuilder.Create("0.1.0");
report.Inventory.AddRange(ReportBuilder.CollectInventory());
report.Findings.Add(new FindingRecord {
    FindingId = "finding-001",
    Code = "VC_MISSING",
    Severity = "warning",
    Title = "VC++ runtime missing",
    Message = "The selected architecture is not registered.",
    BlocksRepair = false
});

ReportPackageResult result = ReportWriter.WritePackage(
    report,
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YushuAfterSales", "Reports"),
    new [] { new DiagnosticLogEntry { Name = "scan", Source = "scanner", Content = "..." } },
    true);
```

The package includes `report.json`, `inventory.json`, `actions.jsonl`, `findings.json`, `environment.json`, `files.json`, `README.html`, and redacted files under `logs/`. `files.json` hashes every package file except itself to avoid a circular self-hash. Add a reference to `System.IO.Compression.FileSystem` for .NET Framework 4.8 ZIP support.

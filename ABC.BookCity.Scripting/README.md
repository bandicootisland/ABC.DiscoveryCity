# ABC.BookCity Scripting

This folder contains generated C# scripts for downloading datasets.

## How to Run

Since these are standalone C# scripts (using top-level statements), they cannot be run directly with `dotnet run` without a project file.

We have provided a helper PowerShell script `Run-Script.ps1` to execute them easily.

### Usage

```powershell
.\Run-Script.ps1 .\Download_Your_Script_Name.cs
```

This will:
1. Create a temporary isolated .NET Console project.
2. Copy your script into it.
3. Execute it using `dotnet run`.

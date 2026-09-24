# Hosting AI Factory on Windows & IIS

This document describes how to deploy and configure AI Factory (API and Worker) on a Windows host or under Internet Information Services (IIS).

---

## 1. Overview & Architecture

AI Factory supports both containerized Linux deployments (via Docker Compose) and native Windows deployments (via IIS or Windows Service).

On Windows:
- SQLite databases, backups, and workspace directories must not use Linux container paths (`/var/lib/ai-factory/...`).
- Instead, Windows deployments use the `%ProgramData%\AI-Factory` path convention (or relative paths like `data/factory.db`).
- Configuration is cleanly decoupled using platform overlays (`appsettings.Production.Windows.json`).

---

## 2. Directory Layout & Storage

By default on Windows, AI Factory stores data under `%ProgramData%\AI-Factory`:

| Component | Default Windows Path | Setting |
| :--- | :--- | :--- |
| **SQLite Database** | `%ProgramData%\AI-Factory\data\factory.db` | `Database:ConnectionString` / `ConnectionStrings:FactoryDatabase` |
| **Backups** | `%ProgramData%\AI-Factory\backups` | `Backup:BackupDirectory` |
| **Agent Workspace** | `%ProgramData%\AI-Factory\workspace` | `AgentWorkspace:ProjectsBaseDirectory` / `AgentWorkspace:AllowedRoot` |
| **Hermes Storage** | `%ProgramData%\AI-Factory\hermes` | `Hermes:DataVolume` |

> [!TIP]
> Storing database files and workspaces in `%ProgramData%\AI-Factory` (rather than inside the IIS web root) ensures that re-publishing with `<DeleteExistingFiles>true</DeleteExistingFiles>` does not delete the database or persistent agent data.

---

## 3. IIS Permissions & Application Pool Identity

IIS worker processes run under a virtual application pool identity (e.g. `IIS AppPool\DefaultAppPool` or `IIS AppPool\AI-Factory`).

### Granting Permissions

Ensure the Application Pool identity has `Modify` (Read, Write, Delete) access to the storage root:

```cmd
icacls "%ProgramData%\AI-Factory" /grant "IIS AppPool\AI-Factory":(OI)(CI)M /T
```

Replace `AI-Factory` with the name of your IIS Application Pool.

If you choose to use an in-tree path such as `D:\Site\AI-Factory\App_Data`, grant permissions to that folder:

```cmd
icacls "D:\Site\AI-Factory\App_Data" /grant "IIS AppPool\AI-Factory":(OI)(CI)M /T
```

---

## 4. Automatic & Manual Database Migrations

### Automatic Migration on Startup (`AutoMigrate`)

In `appsettings.Production.Windows.json`:
```json
{
  "Database": {
    "AutoMigrate": true
  }
}
```
When `Database:AutoMigrate` is `true`, `AI.Factory.Api` automatically applies any pending Entity Framework Core migrations on startup before processing requests.

### Manual CLI Migration (`--migrate-only`)

You can also run database migrations manually using the published assembly:
```powershell
dotnet AI.Factory.Api.dll --migrate-only
```
This applies pending migrations and exits with code 0.

---

## 5. Configuration Overlays

The application uses `AddPlatformSpecificOverlay` to automatically load configuration in the following order of precedence:

1. `appsettings.json` (base settings)
2. `appsettings.Windows.json` (Windows base defaults, optional)
3. `appsettings.Production.json` (Linux container production settings)
4. `appsettings.Production.Windows.json` (Windows production settings)
5. Environment variables (e.g. `Database__ConnectionString`, `OPENROUTER_API_KEY`)

### Example `appsettings.Production.Windows.json`
```json
{
  "ConnectionStrings": {
    "FactoryDatabase": "Data Source=%ProgramData%/AI-Factory/data/factory.db;Cache=Shared"
  },
  "Database": {
    "Provider": "Sqlite",
    "ConnectionString": "Data Source=%ProgramData%/AI-Factory/data/factory.db;Cache=Shared",
    "AutoMigrate": true
  },
  "Backup": {
    "BackupDirectory": "%ProgramData%/AI-Factory/backups"
  },
  "AgentWorkspace": {
    "ProjectsBaseDirectory": "%ProgramData%/AI-Factory/workspace",
    "AllowedRoot": "%ProgramData%/AI-Factory/workspace"
  }
}
```

---

## 6. Troubleshooting Common Issues

### SQLite Error 14: `unable to open database file`
- **Cause 1**: The directory containing `factory.db` does not exist. AI Factory's `SqliteStorageInitializer.EnsureDataDirectoryExists` automatically creates this folder if permissions allow.
- **Cause 2**: The path was configured with Linux path `/var/lib/...` on Windows. Ensure `appsettings.Production.Windows.json` is present and deployed next to `AI.Factory.Api.dll`.
- **Cause 3**: The IIS Application Pool account does not have write access to `%ProgramData%\AI-Factory\data` or the target folder. Check the Event Viewer application log for `SqliteDataDirectoryReady` or `InvalidOperationException` mentioning the hosting identity.

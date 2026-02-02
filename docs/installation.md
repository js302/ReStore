# Installation

## Download Pre-built Installer (Recommended)

1. **[Download the latest MSIX installer](https://github.com/js302/ReStore/releases/latest)**
2. **Double-click the MSIX file** to install ReStore

## Prerequisites

- Windows 10 22H2 or later
- ~250MB free disk space

## Build from Source

**Prerequisites:**

- .NET 9.0 SDK or later
- Windows OS (Windows 10 or later recommended)

1. Clone the repository:

   ```bash
   git clone https://github.com/js302/ReStore.git
   cd ReStore
   ```

2. Build the solution:

   ```bash
   dotnet build ReStore.sln
   ```

3. Run the GUI application:

   ```bash
   dotnet run --project ReStore
   ```

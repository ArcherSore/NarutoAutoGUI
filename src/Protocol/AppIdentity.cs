namespace Protocol;

// The product name where it is visible or persisted at runtime. Project folders and namespaces carry no brand, so
// renaming the product touches this file, AssemblyName in Gui.csproj and Worker.csproj (they produce
// Bunshin.exe and Bunshin.Worker.exe), the icon and the release workflow.
public static class AppIdentity
{
    public const string Name = "Bunshin";
    public const string WorkerFileName = Name + ".Worker.exe";
    public const string CredentialTarget = Name + "/ChildSession/WindowsLogon";
    // Saved by versions released before the rename; removed when the credential is saved again.
    public const string LegacyCredentialTarget = "NarutoAutoGUI/ChildSession/WindowsLogon";
}

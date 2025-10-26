using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;

namespace SoftwareSetupApp.Services;

public static class WindowsShellService
{
    public static bool TryPinToTaskbar(string executablePath, out string message)
    {
        message = string.Empty;

        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            message = "Le fichier exécutable est introuvable.";
            return false;
        }

        object? shell = null;
        object? folder = null;
        object? item = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null)
            {
                message = "Shell.Application n'est pas disponible.";
                return false;
            }

            var directory = Path.GetDirectoryName(executablePath);
            var fileName = Path.GetFileName(executablePath);

            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
            {
                message = "Chemin de l'exécutable invalide.";
                return false;
            }

            shell = Activator.CreateInstance(shellType);
            folder = shellType.InvokeMember("NameSpace", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { directory });
            if (folder == null)
            {
                message = "Impossible d'accéder au dossier contenant l'exécutable.";
                return false;
            }

            item = folder.GetType().InvokeMember("ParseName", System.Reflection.BindingFlags.InvokeMethod, null, folder, new object[] { fileName });
            if (item == null)
            {
                message = "Impossible de récupérer le raccourci de l'exécutable.";
                return false;
            }

            var verbsObject = item.GetType().InvokeMember("Verbs", System.Reflection.BindingFlags.InvokeMethod, null, item, null);
            if (verbsObject is not IEnumerable verbs)
            {
                message = "Impossible de récupérer les options d'épinglage.";
                return false;
            }

            foreach (var verb in verbs)
            {
                var name = verb?.GetType().InvokeMember("Name", System.Reflection.BindingFlags.GetProperty, null, verb, null) as string;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var normalized = name.Replace("&", string.Empty).Trim();
                if (string.Equals(normalized, "Pin to taskbar", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(normalized, "Épingler à la barre des tâches", StringComparison.OrdinalIgnoreCase))
                {
                    verb?.GetType().InvokeMember("DoIt", System.Reflection.BindingFlags.InvokeMethod, null, verb, null);
                    return true;
                }
            }

            message = "L'application est peut-être déjà épinglée à la barre des tâches.";
            return false;
        }
        catch (COMException ex)
        {
            message = $"Erreur COM lors de l'épinglage : {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            message = $"Erreur lors de l'épinglage : {ex.Message}";
            return false;
        }
        finally
        {
            ReleaseComObject(item);
            ReleaseComObject(folder);
            ReleaseComObject(shell);
        }
    }

    private static void ReleaseComObject(object? comObject)
    {
        if (comObject != null && Marshal.IsComObject(comObject))
        {
            Marshal.FinalReleaseComObject(comObject);
        }
    }
}

using System;

namespace SoftwareSetupApp.Models;

public class DefaultAssociation
{
    public DefaultAssociation(string identifier, string progId, string applicationName)
    {
        Identifier = identifier ?? throw new ArgumentNullException(nameof(identifier));
        ProgId = progId ?? throw new ArgumentNullException(nameof(progId));
        ApplicationName = applicationName ?? throw new ArgumentNullException(nameof(applicationName));
    }

    public string Identifier { get; }

    public string ProgId { get; }

    public string ApplicationName { get; }
}

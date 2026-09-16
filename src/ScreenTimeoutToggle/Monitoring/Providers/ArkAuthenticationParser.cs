using System.Text.Json;
using OBDim.Monitoring.Models;
namespace OBDim.Monitoring.Providers;

public static class ArkAuthenticationParser
{
    public static ProviderFailure? Parse(string json)
    {
        try
        {
            using var doc=JsonDocument.Parse(json);
            var root=doc.RootElement;
            if(ProviderFailureClassifier.Envelope(root) is {} failure) return failure;
            var control=root.TryGetProperty("control_plane_auth").TryGetProperty("status").AsNullableString();
            if(control=="needs_login" || root.TryGetProperty("logged_in").AsNullableBool()==false)
                return new(ProviderErrorKind.NotSignedIn,"auth.login_required");
            // ID-token expiry is renewable; only the control-plane result is authoritative.
            if(control=="ok") return null;
            return new(ProviderErrorKind.ParseFailed,"auth.status_unknown");
        }
        catch(JsonException) { return new(ProviderErrorKind.ParseFailed,"auth.invalid_json"); }
    }
}

using System.Reflection;
using LoveDownload.Api.Controllers;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace LoveDownload.Api;

/// <summary>
/// Removes the auth controllers from MVC's controller feature. Registered only when
/// <c>AUTH_ENABLED</c> is false, so a disabled app exposes exactly the pre-auth routes
/// (/downloads, /schedule) and the auth routes simply do not exist. That keeps the
/// default pipeline byte-for-byte what it was before auth was added.
/// </summary>
internal sealed class AuthControllerFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
{
    private static readonly Type[] AuthControllers =
    [
        typeof(AuthController),
        typeof(ProtectedController),
        typeof(PublicController),
    ];

    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        foreach (var controller in AuthControllers)
        {
            feature.Controllers.Remove(controller.GetTypeInfo());
        }
    }
}

using Mechware.Application.Identity;

namespace Mechware.Worker;

/// <summary>
/// The "current user" of the background host: nobody. Infrastructure services that audit the acting staff member
/// (price lists, shipping zones, product admin, catalogue import) take an <see cref="ICurrentUserService"/>; they are
/// only ever called from the API, where it is the signed-in user, but they are registered by the shared setup and so
/// must be constructible in the Worker too (the Development host validates every registration at start-up).
/// It has no user, no permissions and no address, so nothing running in the Worker can act as a person.
/// </summary>
internal sealed class SystemCurrentUserService : ICurrentUserService
{
    public Guid? UserId => null;
    public string? Email => null;
    public Guid? SellerId => null;
    public string? IpAddress => null;
    public string? UserAgent => null;
    public string? CorrelationId => null;
    public bool HasPermission(string permission) => false;
}

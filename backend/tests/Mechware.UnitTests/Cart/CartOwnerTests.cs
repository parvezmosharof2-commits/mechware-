using FluentAssertions;
using Mechware.Application.Cart;
using Mechware.Domain.Common.Exceptions;

namespace Mechware.UnitTests.Cart;

public class CartOwnerTests
{
    [Fact]
    public void UserAndGuest_WithTheSameId_HaveDifferentStoreKeys()
    {
        var id = Guid.NewGuid();
        CartOwner.ForUser(id).StoreKey.Should().NotBe(CartOwner.FromValidatedGuestToken(new GuestCartId(id)).StoreKey);
        CartOwner.ForUser(id).StoreKey.Should().StartWith("cart:u:");
    }

    [Fact]
    public void EmptyUserId_IsRefused() =>
        ((Action)(() => CartOwner.ForUser(Guid.Empty))).Should().Throw<ValidationException>();

    [Fact]
    public void Ttl_GuestIsShorterThanUser() =>
        ICartStore.GuestTtl.Should().BeLessThan(ICartStore.UserTtl);
}

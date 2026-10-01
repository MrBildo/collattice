namespace Collabot.Collattice.Api.Tests.Infrastructure;

// The retry budget the allocators promise: a card-number claim and a description revision each get
// this many attempts at a contended save before the request fails.
//
// It is written here as a number of its own rather than read from the helpers, and that is the whole
// point of it. A test that arms its collisions from the helper's constant moves with the constant, so
// cutting the budget leaves that test green: it arms fewer collisions and still sees the request
// survive one short of the new, smaller budget. Held apart, the number states the behaviour and a cut
// reds the tests that drive the allocators to their last attempt. Changing the budget on purpose means
// changing it here as well, which puts the change in front of a reviewer.
internal static class AllocatorRetryBudget
{
    public const int Attempts = 8;
}

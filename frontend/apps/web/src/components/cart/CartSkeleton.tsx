import { Skeleton } from '@mechware/ui/src/components/Skeleton';

/** Same footprint as the loaded cart (a list of lines and a summary), so the page does not jump when the cart arrives. */
export function CartSkeleton() {
  return (
    <div aria-hidden="true" className="lg:grid lg:grid-cols-[1fr_20rem] lg:gap-8">
      <div className="space-y-3">
        {Array.from({ length: 3 }, (_, i) => (
          <Skeleton key={i} className="h-28 w-full" />
        ))}
      </div>
      <Skeleton className="mt-6 h-56 w-full lg:mt-0" />
    </div>
  );
}

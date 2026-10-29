'use client';

import React from 'react';
import { cn } from '../utils';
import { Skeleton } from './Skeleton';

export interface ProductCardSkeletonProps {
  size?: 'default' | 'compact' | 'horizontal';
  className?: string;
}

export const ProductCardSkeleton: React.FC<ProductCardSkeletonProps> = ({
  size = 'default',
  className,
}) => {
  /* ---------------- HORIZONTAL SKELETON ---------------- */
  if (size === 'horizontal') {
    return (
      <div
        className={cn(
          'flex items-center gap-3.5 rounded-lg border border-surface-border bg-surface p-3 font-sans select-none',
          className
        )}
      >
        <Skeleton className="h-20 w-20 shrink-0 rounded-md" />
        <div className="flex-1 flex flex-col justify-between gap-2 min-w-0">
          <Skeleton className="h-3 w-16 rounded-xs" />
          <Skeleton className="h-4 w-3/4 rounded-xs" />
          <Skeleton className="h-4 w-24 rounded-xs" />
        </div>
        <Skeleton className="h-9 w-9 shrink-0 rounded-md" />
      </div>
    );
  }

  /* ---------------- COMPACT SKELETON ---------------- */
  if (size === 'compact') {
    return (
      <div
        className={cn(
          'flex flex-col rounded-lg border border-surface-border bg-surface p-3 font-sans select-none w-52 shrink-0',
          className
        )}
      >
        <Skeleton className="aspect-square w-full rounded-md mb-2.5" />
        <Skeleton className="h-3 w-14 rounded-xs mb-1.5" />
        <Skeleton className="h-4 w-full rounded-xs mb-1" />
        <Skeleton className="h-4 w-3/4 rounded-xs mb-3" />
        <Skeleton className="h-5 w-20 rounded-xs" />
      </div>
    );
  }

  /* ---------------- DEFAULT SKELETON (ZERO CLS) ---------------- */
  return (
    <div
      className={cn(
        'flex flex-col rounded-xl border border-surface-border bg-surface p-4 font-sans select-none',
        className
      )}
    >
      {/* Top Image Stage identical to loaded card */}
      <Skeleton className="aspect-square w-full rounded-lg mb-3.5" />

      {/* Brand */}
      <Skeleton className="h-3 w-20 rounded-xs mb-1.5" />

      {/* Title 2 lines with min-h-[2.5rem] */}
      <div className="min-h-[2.5rem] flex flex-col justify-center gap-1 mb-2">
        <Skeleton className="h-4 w-full rounded-xs" />
        <Skeleton className="h-4 w-4/5 rounded-xs" />
      </div>

      {/* Rating */}
      <Skeleton className="h-4 w-28 rounded-xs mb-2.5" />

      {/* Price block */}
      <Skeleton className="h-6 w-32 rounded-xs mb-2" />

      {/* Delivery hint */}
      <Skeleton className="h-3.5 w-40 rounded-xs mb-3.5" />

      {/* Quick Add To Cart Button area */}
      <div className="mt-auto pt-2 border-t border-surface-border/40">
        <Skeleton className="h-10 w-full rounded-md" />
      </div>
    </div>
  );
};

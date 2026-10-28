'use client';

import React from 'react';
import { cn } from '../utils';

export function Skeleton({
  className,
  ...props
}: React.HTMLAttributes<HTMLDivElement>) {
  return (
    <div
      className={cn(
        'relative overflow-hidden rounded-md bg-bg-muted/80 before:absolute before:inset-0 before:-translate-x-full before:animate-[shimmer_1.8s_infinite] before:bg-gradient-to-r before:from-transparent before:via-surface-border/50 before:to-transparent',
        className
      )}
      {...props}
    />
  );
}

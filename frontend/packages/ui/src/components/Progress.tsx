'use client';

import React from 'react';
import * as ProgressPrimitive from '@radix-ui/react-progress';
import { cn } from '../utils';

export interface ProgressProps
  extends React.ComponentPropsWithoutRef<typeof ProgressPrimitive.Root> {
  variant?: 'accent' | 'success' | 'warning' | 'fault';
  showLabel?: boolean;
}

const variantIndicatorStyles = {
  accent: 'bg-accent shadow-glow-cyan-sm',
  success: 'bg-status-online',
  warning: 'bg-status-pending',
  fault: 'bg-fault',
};

export const Progress = React.forwardRef<
  React.ElementRef<typeof ProgressPrimitive.Root>,
  ProgressProps
>(({ className, value = 0, variant = 'accent', showLabel = false, ...props }, ref) => {
  const percentage = Math.min(Math.max(value || 0, 0), 100);

  return (
    <div className="w-full flex flex-col gap-1.5 font-sans">
      {showLabel && (
        <div className="flex justify-between items-center text-xs font-semibold text-text-muted">
          <span>Progress</span>
          <span className="tabular-nums text-text">{percentage}%</span>
        </div>
      )}
      <ProgressPrimitive.Root
        ref={ref}
        className={cn(
          'relative h-2 w-full overflow-hidden rounded-full bg-bg-muted border border-surface-border/50',
          className
        )}
        {...props}
      >
        <ProgressPrimitive.Indicator
          className={cn(
            'h-full w-full flex-1 transition-all duration-base',
            variantIndicatorStyles[variant]
          )}
          style={{ transform: `translateX(-${100 - percentage}%)` }}
        />
      </ProgressPrimitive.Root>
    </div>
  );
});
Progress.displayName = ProgressPrimitive.Root.displayName;

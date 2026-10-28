'use client';

import React from 'react';
import { Loader2 } from 'lucide-react';
import { cn } from '../utils';

export interface IconButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  'aria-label': string;
  variant?: 'accent' | 'primary' | 'secondary' | 'outline' | 'ghost' | 'destructive';
  size?: 'sm' | 'md' | 'lg';
  isLoading?: boolean;
}

const variantStyles: Record<NonNullable<IconButtonProps['variant']>, string> = {
  accent:
    'bg-accent text-accent-fg hover:bg-accent-hover hover:shadow-glow-cyan active:scale-[0.96] focus-visible:ring-accent border border-transparent shadow-sm',
  primary:
    'bg-primary text-primary-fg hover:opacity-90 active:scale-[0.96] focus-visible:ring-accent border border-transparent',
  secondary:
    'bg-bg-muted text-text hover:bg-surface-border active:scale-[0.96] focus-visible:ring-accent border border-surface-border',
  outline:
    'border border-surface-border text-text hover:border-accent hover:text-accent active:scale-[0.96] focus-visible:ring-accent bg-transparent',
  ghost:
    'text-text hover:bg-bg-muted hover:text-accent active:scale-[0.96] focus-visible:ring-accent bg-transparent',
  destructive:
    'bg-fault text-white hover:opacity-90 active:scale-[0.96] focus-visible:ring-fault border border-transparent',
};

const sizeStyles: Record<NonNullable<IconButtonProps['size']>, string> = {
  sm: 'h-8 w-8 text-sm rounded-sm',
  md: 'h-10 w-10 text-base rounded-md',
  lg: 'h-12 w-12 text-lg rounded-md',
};

export const IconButton = React.forwardRef<HTMLButtonElement, IconButtonProps>(
  (
    {
      className,
      variant = 'secondary',
      size = 'md',
      isLoading = false,
      disabled,
      children,
      ...props
    },
    ref
  ) => {
    return (
      <button
        ref={ref}
        disabled={disabled || isLoading}
        className={cn(
          'inline-flex items-center justify-center transition-all duration-fast select-none outline-none focus-visible:ring-2 focus-visible:ring-offset-2 focus-visible:ring-offset-bg disabled:opacity-50 disabled:pointer-events-none disabled:active:scale-100',
          variantStyles[variant],
          sizeStyles[size],
          className
        )}
        {...props}
      >
        {isLoading ? <Loader2 className="animate-spin h-4 w-4" /> : children}
      </button>
    );
  }
);
IconButton.displayName = 'IconButton';

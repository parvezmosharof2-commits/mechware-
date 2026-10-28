'use client';

import React from 'react';
import { Loader2 } from 'lucide-react';
import { cn } from '../utils';

export interface ButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: 'accent' | 'primary' | 'secondary' | 'outline' | 'ghost' | 'destructive' | 'link';
  size?: 'sm' | 'md' | 'lg' | 'xl';
  isLoading?: boolean;
  leftIcon?: React.ReactNode;
  rightIcon?: React.ReactNode;
}

const variantStyles: Record<NonNullable<ButtonProps['variant']>, string> = {
  accent:
    'bg-accent text-accent-fg font-semibold hover:bg-accent-hover hover:shadow-glow-cyan active:scale-[0.98] focus-visible:ring-accent border border-transparent shadow-sm',
  primary:
    'bg-primary text-primary-fg font-semibold hover:opacity-90 active:scale-[0.98] focus-visible:ring-accent border border-transparent',
  secondary:
    'bg-bg-muted text-text hover:bg-surface-border active:scale-[0.98] focus-visible:ring-accent border border-surface-border',
  outline:
    'border border-surface-border text-text hover:border-accent hover:text-accent active:scale-[0.98] focus-visible:ring-accent bg-transparent',
  ghost:
    'text-text hover:bg-bg-muted hover:text-accent active:scale-[0.98] focus-visible:ring-accent bg-transparent',
  destructive:
    'bg-fault text-white font-semibold hover:opacity-90 active:scale-[0.98] focus-visible:ring-fault shadow-sm border border-transparent',
  link:
    'text-accent underline-offset-4 hover:underline p-0 h-auto font-medium focus-visible:ring-accent bg-transparent',
};

const sizeStyles: Record<NonNullable<ButtonProps['size']>, string> = {
  sm: 'h-8 px-3 text-xs rounded-sm gap-1.5',
  md: 'h-10 px-4 text-sm rounded-md gap-2',
  lg: 'h-12 px-6 text-base rounded-md gap-2.5',
  xl: 'h-14 px-8 text-lg rounded-lg gap-3 font-bold',
};

export const Button = React.forwardRef<HTMLButtonElement, ButtonProps>(
  (
    {
      className,
      variant = 'primary',
      size = 'md',
      isLoading = false,
      leftIcon,
      rightIcon,
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
          'inline-flex items-center justify-center font-medium transition-all duration-fast select-none whitespace-nowrap outline-none focus-visible:ring-2 focus-visible:ring-offset-2 focus-visible:ring-offset-bg disabled:opacity-50 disabled:pointer-events-none disabled:active:scale-100',
          variantStyles[variant],
          variant !== 'link' && sizeStyles[size],
          className
        )}
        {...props}
      >
        {isLoading && <Loader2 className="animate-spin shrink-0 h-4 w-4" />}
        {!isLoading && leftIcon && <span className="shrink-0">{leftIcon}</span>}
        <span>{children}</span>
        {!isLoading && rightIcon && <span className="shrink-0">{rightIcon}</span>}
      </button>
    );
  }
);
Button.displayName = 'Button';

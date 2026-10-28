'use client';

import React from 'react';
import * as AvatarPrimitive from '@radix-ui/react-avatar';
import { ShieldCheck } from 'lucide-react';
import { cn } from '../utils';

export interface AvatarProps
  extends React.ComponentPropsWithoutRef<typeof AvatarPrimitive.Root> {
  status?: 'online' | 'offline' | 'busy';
  isVerified?: boolean;
}

const statusStyles = {
  online: 'bg-status-online ring-bg',
  offline: 'bg-text-subtle ring-bg',
  busy: 'bg-fault ring-bg',
};

export const Avatar = React.forwardRef<
  React.ElementRef<typeof AvatarPrimitive.Root>,
  AvatarProps
>(({ className, status, isVerified, children, ...props }, ref) => (
  <div className="relative inline-flex shrink-0">
    <AvatarPrimitive.Root
      ref={ref}
      className={cn(
        'relative flex h-10 w-10 shrink-0 overflow-hidden rounded-full border border-surface-border bg-bg-muted font-sans select-none',
        className
      )}
      {...props}
    >
      {children}
    </AvatarPrimitive.Root>

    {status && (
      <span
        className={cn(
          'absolute bottom-0 right-0 h-2.5 w-2.5 rounded-full ring-2',
          statusStyles[status]
        )}
      />
    )}

    {isVerified && (
      <span
        className="absolute -top-1 -right-1 bg-brand-navy rounded-full p-0.5 text-accent shadow-sm"
        title="Verified Enterprise Partner"
      >
        <ShieldCheck className="h-3.5 w-3.5 fill-current" />
      </span>
    )}
  </div>
));
Avatar.displayName = AvatarPrimitive.Root.displayName;

export const AvatarImage = React.forwardRef<
  React.ElementRef<typeof AvatarPrimitive.Image>,
  React.ComponentPropsWithoutRef<typeof AvatarPrimitive.Image>
>(({ className, ...props }, ref) => (
  <AvatarPrimitive.Image
    ref={ref}
    className={cn('aspect-square h-full w-full object-cover', className)}
    {...props}
  />
));
AvatarImage.displayName = AvatarPrimitive.Image.displayName;

export const AvatarFallback = React.forwardRef<
  React.ElementRef<typeof AvatarPrimitive.Fallback>,
  React.ComponentPropsWithoutRef<typeof AvatarPrimitive.Fallback>
>(({ className, ...props }, ref) => (
  <AvatarPrimitive.Fallback
    ref={ref}
    className={cn(
      'flex h-full w-full items-center justify-center rounded-full bg-bg-subtle text-xs font-semibold text-text uppercase',
      className
    )}
    {...props}
  />
));
AvatarFallback.displayName = AvatarPrimitive.Fallback.displayName;

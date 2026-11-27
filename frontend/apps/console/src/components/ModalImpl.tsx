'use client';

import * as React from 'react';
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@mechware/ui/src/components/Dialog';
import type { ModalProps } from './Modal';

/** The Radix dialog behind `Modal`; only fetched when a modal is first opened. */
export function ModalImpl({ open, onOpenChange, title, description, descriptionTestId, contentTestId, contentClassName, children }: ModalProps) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent data-testid={contentTestId} className={contentClassName}>
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription data-testid={descriptionTestId}>{description}</DialogDescription>
        </DialogHeader>
        {children}
      </DialogContent>
    </Dialog>
  );
}

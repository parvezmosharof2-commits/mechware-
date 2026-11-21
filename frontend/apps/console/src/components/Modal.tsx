'use client';

import * as React from 'react';
import { lazyDialog } from '../lib/lazyDialog';

export interface ModalProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: React.ReactNode;
  description: React.ReactNode;
  descriptionTestId?: string;
  contentTestId?: string;
  children?: React.ReactNode;
}

/**
 * A confirm / form dialog whose Radix code loads when it is first opened (keeps the console routes under the 170 kB
 * first-load budget). Same markup as `Dialog` + `DialogContent` + `DialogHeader`; put a `ModalFooter` at the end.
 */
export const Modal = lazyDialog<ModalProps>(() => import('./ModalImpl').then((m) => ({ default: m.ModalImpl })));

/** Same classes as the UI package's `DialogFooter`, without importing the dialog code. */
export function ModalFooter({ children }: { children: React.ReactNode }) {
  return <div className="flex flex-col-reverse sm:flex-row sm:justify-end sm:space-x-2 pt-4 border-t border-surface-border mt-4">{children}</div>;
}

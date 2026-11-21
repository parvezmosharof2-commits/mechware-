'use client';

import * as React from 'react';
import { lazyDialog } from '../lib/lazyDialog';
import type { StepUpDialogImpl } from './StepUpDialogImpl';

/** The authenticator-code dialog; its code loads when it is first opened (see `StepUpDialogImpl`). */
export const StepUpDialog = lazyDialog<React.ComponentProps<typeof StepUpDialogImpl>>(() =>
  import('./StepUpDialogImpl').then((m) => ({ default: m.StepUpDialogImpl })));

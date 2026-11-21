'use client';

import * as React from 'react';
import { lazyDialog } from '../lib/lazyDialog';
import type { ReasonDialogImpl } from './ReasonDialogImpl';

/** The reason dialog (5 to 500 characters); its code loads when it is first opened (see `ReasonDialogImpl`). */
export const ReasonDialog = lazyDialog<React.ComponentProps<typeof ReasonDialogImpl>>(() =>
  import('./ReasonDialogImpl').then((m) => ({ default: m.ReasonDialogImpl })));

import { AuthFlowSurface } from '../../../../components/auth/AuthModal';
import { sanitizeReturnToPath } from '../../../../middleware';

interface LoginPageProps {
  params: Promise<{ locale: string }>;
  searchParams?: Promise<{ returnTo?: string }>;
}

export default async function LoginPage({ params, searchParams }: LoginPageProps) {
  const resolvedParams = await params;
  const resolvedSearch = searchParams ? await searchParams : {};
  const locale = resolvedParams.locale === 'bn' ? 'bn' : 'en';
  const returnTo = sanitizeReturnToPath(resolvedSearch.returnTo);

  return (
    <main id="main" className="flex min-h-[70vh] flex-col items-center justify-center bg-bg-canvas p-6">
      <AuthFlowSurface locale={locale} initialMode="signin" returnTo={returnTo} />
    </main>
  );
}

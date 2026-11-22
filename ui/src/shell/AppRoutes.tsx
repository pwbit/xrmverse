import { Body1 } from '@fluentui/react-components';
import { Navigate, Outlet, Route, Routes } from 'react-router-dom';
import { adminRoutes, type AdminRoute } from '../admin/routes';
import { AppPicker } from '../apps/AppPicker';
import { AppShell } from '../apps/AppShell';
import { StudioRoutes } from '../studio/StudioRoutes';
import { UserSettingsGate } from '../apps/userSettings';
import { AccessGate } from './auth/AccessGate';
import { useAccess } from './auth/roles';
import { t } from './i18n/t';
import { NotFound } from './layout/NotFound';
import { Shell } from './layout/Shell';
import { PageHeader } from './layout/PageHeader';

function AdminOnly() {
  return (
    <section>
      <PageHeader title={t('route.adminOnly.title')} />
      <Body1 block>{t('route.adminOnly.body')}</Body1>
    </section>
  );
}

function Guarded({ route }: { route: AdminRoute }) {
  const { level } = useAccess();
  return route.adminOnly && level !== 'admin' ? <AdminOnly /> : <>{route.element}</>;
}

export function AppRoutes() {
  return (
    <Routes>
      <Route
        element={
          <AccessGate anyUser>
            <UserSettingsGate>
              <Outlet />
            </UserSettingsGate>
          </AccessGate>
        }
      >
        <Route path="/" element={<Navigate to="/apps" replace />} />
        <Route path="/apps" element={<AppPicker />} />
        <Route path="/main.aspx" element={<AppShell />} />
        <Route path="/studio/*" element={<StudioRoutes />} />
      </Route>
      <Route
        element={
          <AccessGate>
            <Shell />
          </AccessGate>
        }
      >
        {adminRoutes.map((r) => (
          <Route key={r.path} path={r.path} element={<Guarded route={r} />} />
        ))}
        <Route path="*" element={<NotFound />} />
      </Route>
    </Routes>
  );
}

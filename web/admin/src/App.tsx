import { AdminApp } from './views/AdminApp'
import { LoginView } from './views/LoginView'
import { SetupView } from './views/SetupView'
import type { PageView } from './pageInfo'

export function App({ view, version }: { view: PageView; version: string }) {
  switch (view) {
    case 'setup':
      return <SetupView />
    case 'refused':
      return <LoginView refused />
    case 'app':
      return <AdminApp version={version} />
    default:
      return <LoginView />
  }
}

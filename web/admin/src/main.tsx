import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { App } from './App'
import { readPageView, readVersion } from './pageInfo'
import './styles.css'

const root = document.getElementById('root')
if (root) {
  createRoot(root).render(
    <StrictMode>
      <App view={readPageView()} version={readVersion()} />
    </StrictMode>,
  )
}

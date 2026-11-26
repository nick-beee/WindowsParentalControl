import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { readRequestData } from './data'
import { RequestApp } from './RequestApp'
import './request.css'

// The service always sends the data (RequestSpa.cs). Without it the page has no texts, so it shows nothing.
const root = document.getElementById('root')
const data = readRequestData()
if (root && data) {
  createRoot(root).render(
    <StrictMode>
      <RequestApp data={data} />
    </StrictMode>,
  )
} else {
  console.error('The page has no data (meta tag pc-request-data).')
}

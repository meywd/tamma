import { Hono } from 'hono'
import authRoutes from './routes/auth.routes'

const app = new Hono()

app.get('/', (c) => {
  return c.text('Hello Hono!')
})

app.route('/auth', authRoutes)

export default app

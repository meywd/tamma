import { beforeAll, afterEach, afterAll } from 'vitest'
import {
  migrateTestDatabase,
  cleanupTestDatabase,
  closeTestDatabase,
} from './test-utils/db'

beforeAll(async () => {
  await migrateTestDatabase()
})

afterEach(async () => {
  await cleanupTestDatabase()
})

afterAll(async () => {
  await closeTestDatabase()
})

import { knex, Knex } from 'knex'
import config from '../../../../knexfile'
import fs from 'fs'
import path from 'path'

function parseEnvFile(filePath: string) {
  const data = fs.readFileSync(filePath, 'utf-8')
  const lines = data.split('\n')
')
  const env: { [key: string]: string } = {}
  for (const line of lines) {
    if (line.startsWith('#') || !line.includes('=')) {
      continue
    }
    const [key, value] = line.split('=', 2)
    env[key] = value.trim()
  }
  return env
}

let db: Knex | null = null

export async function getTestDatabase() {
  if (db) {
    return db
  }

  const envPath = path.resolve(__dirname, '../../../../.env.database')
  const env = parseEnvFile(envPath)

  // Set the environment variables for the test database
  process.env.DB_HOST = env.DB_HOST
  process.env.DB_PORT = env.DB_PORT
  process.env.DB_NAME = 'test_platform_test' // Ensure test database is used
  process.env.DB_USER = env.DB_USER
  process.env.DB_PASSWORD = env.DB_PASSWORD

  const knexInstance = knex(config.test)
  db = knexInstance
  return db
}

export async function migrateTestDatabase() {
  const testDb = await getTestDatabase()
  await testDb.migrate.latest()
}

export async function cleanupTestDatabase() {
  const testDb = await getTestDatabase()
  const tables = await testDb
    .select('tablename')
    .from('pg_tables')
    .where('schemaname', 'public')
    .then((rows) => rows.map((r) => r.tablename))
    .filter((name) => !name.startsWith('knex_'))

  if (tables.length > 0) {
    await testDb.raw(`TRUNCATE TABLE ${tables.join(', ')} RESTART IDENTITY CASCADE`)
  }
}

export async function closeTestDatabase() {
  if (db) {
    await db.destroy()
    db = null
  }
}

import Database from 'better-sqlite3';
import { join } from 'path';
import { homedir } from 'os';
import { mkdirSync } from 'fs';

// Database path (stored in user home directory)
const DB_DIR = join(homedir(), '.mcp-revit');
mkdirSync(DB_DIR, { recursive: true });
const DB_PATH = join(DB_DIR, 'revit-data.db');

let db: Database.Database | null = null;

// Initialize database connection (synchronous — no async/WASM setup needed)
export function getDatabase(): Database.Database {
  if (db) return db;

  db = new Database(DB_PATH);
  db.pragma('journal_mode = WAL');
  db.pragma('foreign_keys = ON');
  initializeDatabase(db);

  return db;
}

// Initialize database schema
function initializeDatabase(database: Database.Database) {
  database.exec(`
    CREATE TABLE IF NOT EXISTS projects (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      project_name TEXT NOT NULL,
      project_path TEXT,
      project_number TEXT,
      project_address TEXT,
      client_name TEXT,
      project_status TEXT,
      author TEXT,
      timestamp INTEGER NOT NULL,
      last_updated INTEGER NOT NULL,
      metadata TEXT
    );

    CREATE TABLE IF NOT EXISTS rooms (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      project_id INTEGER NOT NULL,
      room_id TEXT NOT NULL,
      room_name TEXT,
      room_number TEXT,
      department TEXT,
      level TEXT,
      area REAL,
      perimeter REAL,
      occupancy TEXT,
      comments TEXT,
      timestamp INTEGER NOT NULL,
      metadata TEXT,
      FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE,
      UNIQUE(project_id, room_id)
    );

    CREATE INDEX IF NOT EXISTS idx_projects_name      ON projects(project_name);
    CREATE INDEX IF NOT EXISTS idx_projects_timestamp ON projects(timestamp);
    CREATE INDEX IF NOT EXISTS idx_rooms_project_id   ON rooms(project_id);
    CREATE INDEX IF NOT EXISTS idx_rooms_room_number  ON rooms(room_number);
  `);
}

function getDb(): Database.Database {
  if (!db) throw new Error("Database not initialized. Call getDatabase() first.");
  return db;
}

// Run a write statement (better-sqlite3 writes are synchronous and go straight to WAL)
export function dbRun(sql: string, params?: any[]): void {
  const stmt = getDb().prepare(sql);
  params ? stmt.run(...params) : stmt.run();
}

// Get one row
export function dbGet(sql: string, params?: any[]): any {
  const stmt = getDb().prepare(sql);
  return params ? stmt.get(...params) : stmt.get();
}

// Get all rows
export function dbAll(sql: string, params?: any[]): any[] {
  const stmt = getDb().prepare(sql);
  return (params ? stmt.all(...params) : stmt.all()) as any[];
}

// Get last insert rowid
export function dbLastInsertRowid(): number {
  const row = getDb().prepare('SELECT last_insert_rowid() as id').get() as { id: number | bigint } | undefined;
  return Number(row?.id ?? 0);
}

// Graceful shutdown
function cleanup() {
  if (db) {
    db.close();
    db = null;
  }
}
process.on('exit', cleanup);
process.on('SIGTERM', () => { cleanup(); process.exit(0); });
process.on('SIGINT', () => { cleanup(); process.exit(0); });

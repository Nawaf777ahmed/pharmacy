// ============================================================
// PharmaSmart ERP - طبقة قاعدة البيانات المحلية (IndexedDB)
// ============================================================
const DB_NAME = 'PharmaSmartOfflineDB';
const DB_VERSION = 1;
const STORE_DRUGS = 'drugs_cache';
const STORE_PENDING = 'pending_sales';

let db;

function openOfflineDB() {
    return new Promise((resolve, reject) => {
        if (db) { resolve(db); return; }

        const req = indexedDB.open(DB_NAME, DB_VERSION);

        req.onupgradeneeded = e => {
            const database = e.target.result;
            if (!database.objectStoreNames.contains(STORE_DRUGS)) {
                database.createObjectStore(STORE_DRUGS, { keyPath: 'id' });
            }
            if (!database.objectStoreNames.contains(STORE_PENDING)) {
                database.createObjectStore(STORE_PENDING, { keyPath: 'offlineId', autoIncrement: true });
            }
        };

        req.onsuccess = e => { db = e.target.result; resolve(db); };
        req.onerror = e => reject(e.target.error);
    });
}

// ------- بيانات الأدوية -------

async function saveDrugsToCache(drugsArray) {
    const database = await openOfflineDB();
    const tx = database.transaction(STORE_DRUGS, 'readwrite');
    const store = tx.objectStore(STORE_DRUGS);
    drugsArray.forEach(drug => store.put(drug));
    return new Promise((resolve, reject) => {
        tx.oncomplete = () => resolve();
        tx.onerror = e => reject(e.target.error);
    });
}

async function getDrugsFromCache() {
    const database = await openOfflineDB();
    return new Promise((resolve, reject) => {
        const tx = database.transaction(STORE_DRUGS, 'readonly');
        const store = tx.objectStore(STORE_DRUGS);
        const req = store.getAll();
        req.onsuccess = () => resolve(req.result);
        req.onerror = e => reject(e.target.error);
    });
}

// ------- الفواتير المعلقة -------

async function savePendingSale(saleData) {
    const database = await openOfflineDB();
    return new Promise((resolve, reject) => {
        const tx = database.transaction(STORE_PENDING, 'readwrite');
        const store = tx.objectStore(STORE_PENDING);
        const req = store.add({ ...saleData, savedAt: new Date().toISOString() });
        req.onsuccess = () => resolve(req.result);
        req.onerror = e => reject(e.target.error);
    });
}

async function getAllPendingSales() {
    const database = await openOfflineDB();
    return new Promise((resolve, reject) => {
        const tx = database.transaction(STORE_PENDING, 'readonly');
        const store = tx.objectStore(STORE_PENDING);
        const req = store.getAll();
        req.onsuccess = () => resolve(req.result);
        req.onerror = e => reject(e.target.error);
    });
}

async function deletePendingSale(offlineId) {
    const database = await openOfflineDB();
    return new Promise((resolve, reject) => {
        const tx = database.transaction(STORE_PENDING, 'readwrite');
        const store = tx.objectStore(STORE_PENDING);
        const req = store.delete(offlineId);
        req.onsuccess = () => resolve();
        req.onerror = e => reject(e.target.error);
    });
}

async function getPendingSalesCount() {
    const database = await openOfflineDB();
    return new Promise((resolve, reject) => {
        const tx = database.transaction(STORE_PENDING, 'readonly');
        const store = tx.objectStore(STORE_PENDING);
        const req = store.count();
        req.onsuccess = () => resolve(req.result);
        req.onerror = () => resolve(0);
    });
}

// ------- محرك المزامنة -------

async function syncPendingSales(antiForgeryToken) {
    const pending = await getAllPendingSales();
    if (pending.length === 0) return { synced: 0, failed: 0 };

    let synced = 0, failed = 0;

    for (const sale of pending) {
        try {
            const formData = new FormData();
            formData.append('__RequestVerificationToken', antiForgeryToken);
            formData.append('saleJson', JSON.stringify(sale));

            const res = await fetch('/Sales/SyncOfflineSale', {
                method: 'POST',
                body: formData
            });

            if (res.ok) {
                await deletePendingSale(sale.offlineId);
                synced++;
            } else {
                failed++;
            }
        } catch {
            failed++;
        }
    }
    return { synced, failed };
}

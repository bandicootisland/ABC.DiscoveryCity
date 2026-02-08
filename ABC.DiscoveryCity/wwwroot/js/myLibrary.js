// My Library - IndexedDB Storage for BookCity
// Stores book metadata and optional PDF blobs

const DB_NAME = 'BookCityLibrary';
const DB_VERSION = 1;
const STORE_BOOKS = 'books';
const STORE_PDFS = 'pdfs';

let db = null;

async function openDatabase() {
    if (db) return db;
    
    return new Promise((resolve, reject) => {
        const request = indexedDB.open(DB_NAME, DB_VERSION);
        
        request.onerror = () => reject(request.error);
        
        request.onsuccess = () => {
            db = request.result;
            resolve(db);
        };
        
        request.onupgradeneeded = (event) => {
            const database = event.target.result;
            
            // Books store - metadata and reading progress
            if (!database.objectStoreNames.contains(STORE_BOOKS)) {
                const bookStore = database.createObjectStore(STORE_BOOKS, { keyPath: 'htid' });
                bookStore.createIndex('addedDate', 'addedDate', { unique: false });
                bookStore.createIndex('lastRead', 'lastRead', { unique: false });
                bookStore.createIndex('title', 'title', { unique: false });
            }
            
            // PDFs store - binary blobs (separate for performance)
            if (!database.objectStoreNames.contains(STORE_PDFS)) {
                database.createObjectStore(STORE_PDFS, { keyPath: 'htid' });
            }
        };
    });
}

// Add a book to library
window.myLibrary = {
    
    addBook: async function(book) {
        const database = await openDatabase();
        return new Promise((resolve, reject) => {
            const tx = database.transaction(STORE_BOOKS, 'readwrite');
            const store = tx.objectStore(STORE_BOOKS);
            
            // Add timestamps
            book.addedDate = book.addedDate || new Date().toISOString();
            book.lastRead = null;
            book.readProgress = 0;
            book.hasPdf = false;
            
            const request = store.put(book);
            request.onsuccess = () => resolve(true);
            request.onerror = () => reject(request.error);
        });
    },
    
    removeBook: async function(htid) {
        const database = await openDatabase();
        return new Promise((resolve, reject) => {
            const tx = database.transaction([STORE_BOOKS, STORE_PDFS], 'readwrite');
            
            // Remove from both stores
            tx.objectStore(STORE_BOOKS).delete(htid);
            tx.objectStore(STORE_PDFS).delete(htid);
            
            tx.oncomplete = () => resolve(true);
            tx.onerror = () => reject(tx.error);
        });
    },
    
    getBook: async function(htid) {
        const database = await openDatabase();
        return new Promise((resolve, reject) => {
            const tx = database.transaction(STORE_BOOKS, 'readonly');
            const store = tx.objectStore(STORE_BOOKS);
            const request = store.get(htid);
            request.onsuccess = () => resolve(request.result || null);
            request.onerror = () => reject(request.error);
        });
    },
    
    getAllBooks: async function() {
        const database = await openDatabase();
        return new Promise((resolve, reject) => {
            const tx = database.transaction(STORE_BOOKS, 'readonly');
            const store = tx.objectStore(STORE_BOOKS);
            const request = store.getAll();
            request.onsuccess = () => resolve(request.result || []);
            request.onerror = () => reject(request.error);
        });
    },
    
    isInLibrary: async function(htid) {
        const book = await this.getBook(htid);
        return book !== null;
    },
    
    updateReadProgress: async function(htid, page, totalPages) {
        const database = await openDatabase();
        const book = await this.getBook(htid);
        if (!book) return false;
        
        return new Promise((resolve, reject) => {
            const tx = database.transaction(STORE_BOOKS, 'readwrite');
            const store = tx.objectStore(STORE_BOOKS);
            
            book.lastRead = new Date().toISOString();
            book.currentPage = page;
            book.totalPages = totalPages;
            book.readProgress = totalPages > 0 ? Math.round((page / totalPages) * 100) : 0;
            
            const request = store.put(book);
            request.onsuccess = () => resolve(true);
            request.onerror = () => reject(request.error);
        });
    },
    
    // PDF storage
    savePdf: async function(htid, pdfBlob) {
        const database = await openDatabase();
        
        // Update book record
        const book = await this.getBook(htid);
        if (book) {
            book.hasPdf = true;
            book.pdfSize = pdfBlob.size;
            const bookTx = database.transaction(STORE_BOOKS, 'readwrite');
            bookTx.objectStore(STORE_BOOKS).put(book);
        }
        
        return new Promise((resolve, reject) => {
            const tx = database.transaction(STORE_PDFS, 'readwrite');
            const store = tx.objectStore(STORE_PDFS);
            
            const request = store.put({ htid: htid, pdf: pdfBlob, savedDate: new Date().toISOString() });
            request.onsuccess = () => resolve(true);
            request.onerror = () => reject(request.error);
        });
    },
    
    getPdf: async function(htid) {
        const database = await openDatabase();
        return new Promise((resolve, reject) => {
            const tx = database.transaction(STORE_PDFS, 'readonly');
            const store = tx.objectStore(STORE_PDFS);
            const request = store.get(htid);
            request.onsuccess = () => {
                if (request.result && request.result.pdf) {
                    resolve(request.result.pdf);
                } else {
                    resolve(null);
                }
            };
            request.onerror = () => reject(request.error);
        });
    },
    
    // Get a blob URL for inline PDF viewing
    getPdfObjectUrl: async function(htid) {
        const pdfBlob = await this.getPdf(htid);
        if (pdfBlob) {
            return URL.createObjectURL(pdfBlob);
        }
        return null;
    },
    
    hasPdf: async function(htid) {
        const pdf = await this.getPdf(htid);
        return pdf !== null;
    },
    
    deletePdf: async function(htid) {
        const database = await openDatabase();
        
        // Update book record
        const book = await this.getBook(htid);
        if (book) {
            book.hasPdf = false;
            book.pdfSize = null;
            const bookTx = database.transaction(STORE_BOOKS, 'readwrite');
            bookTx.objectStore(STORE_BOOKS).put(book);
        }
        
        return new Promise((resolve, reject) => {
            const tx = database.transaction(STORE_PDFS, 'readwrite');
            const store = tx.objectStore(STORE_PDFS);
            const request = store.delete(htid);
            request.onsuccess = () => resolve(true);
            request.onerror = () => reject(request.error);
        });
    },
    
    // Get library stats
    getStats: async function() {
        const books = await this.getAllBooks();
        const totalBooks = books.length;
        const booksWithPdf = books.filter(b => b.hasPdf).length;
        const totalPdfSize = books.reduce((sum, b) => sum + (b.pdfSize || 0), 0);
        const recentlyRead = books.filter(b => b.lastRead).sort((a, b) => 
            new Date(b.lastRead) - new Date(a.lastRead)
        ).slice(0, 10);
        
        return {
            totalBooks,
            booksWithPdf,
            totalPdfSize,
            recentlyRead
        };
    },
    
    // Open PDF in new tab (for viewing)
    openPdf: async function(htid) {
        const pdfBlob = await this.getPdf(htid);
        if (pdfBlob) {
            const url = URL.createObjectURL(pdfBlob);
            window.open(url, '_blank');
            // Note: URL should be revoked after use, but we leave it for the new tab
            return true;
        }
        return false;
    },
    
    // Clear entire library
    clearAll: async function() {
        const database = await openDatabase();
        return new Promise((resolve, reject) => {
            const tx = database.transaction([STORE_BOOKS, STORE_PDFS], 'readwrite');
            tx.objectStore(STORE_BOOKS).clear();
            tx.objectStore(STORE_PDFS).clear();
            tx.oncomplete = () => resolve(true);
            tx.onerror = () => reject(tx.error);
        });
    },

    // Browse for a PDF file and import it into IndexedDB
    browsePdf: async function(htid) {
        try {
            // Open file picker for PDF
            const [fileHandle] = await window.showOpenFilePicker({
                types: [{
                    description: 'PDF Files',
                    accept: { 'application/pdf': ['.pdf'] }
                }],
                multiple: false
            });
            
            const file = await fileHandle.getFile();
            const blob = await file.arrayBuffer().then(ab => new Blob([ab], { type: 'application/pdf' }));
            
            // Save to IndexedDB
            await this.savePdf(htid, blob);
            
            return { success: true, fileName: file.name, size: file.size };
        } catch (err) {
            if (err.name === 'AbortError') {
                return { success: false, cancelled: true };
            }
            throw err;
        }
    },

    // Store handle to the books catalog folder
    _booksFolderHandle: null,

    // Pick a folder for cataloging books
    pickBooksFolder: async function() {
        try {
            this._booksFolderHandle = await window.showDirectoryPicker({
                mode: 'readwrite',
                startIn: 'documents'
            });
            
            // Store folder name for display
            localStorage.setItem('booksFolderName', this._booksFolderHandle.name);
            
            return { success: true, folderName: this._booksFolderHandle.name };
        } catch (err) {
            if (err.name === 'AbortError') {
                return { success: false, cancelled: true };
            }
            throw err;
        }
    },

    // Get the saved books folder name
    getBooksFolderName: function() {
        return localStorage.getItem('booksFolderName') || null;
    },

    // Catalog a PDF - copy from IndexedDB to the books folder with proper naming
    catalogPdf: async function(htid, title, author) {
        if (!this._booksFolderHandle) {
            // Try to pick folder if not set
            const result = await this.pickBooksFolder();
            if (!result.success) {
                return { success: false, error: 'No books folder selected' };
            }
        }

        // Get PDF from IndexedDB
        const pdfBlob = await this.getPdf(htid);
        if (!pdfBlob) {
            return { success: false, error: 'No PDF found for this book' };
        }

        // Create a safe filename from title and author
        const safeTitle = (title || 'Unknown').replace(/[<>:"/\\|?*]/g, '_').substring(0, 100);
        const safeAuthor = (author || 'Unknown').replace(/[<>:"/\\|?*]/g, '_').substring(0, 50);
        const fileName = `${safeAuthor} - ${safeTitle}.pdf`;

        try {
            // Request permission again if needed
            const permission = await this._booksFolderHandle.requestPermission({ mode: 'readwrite' });
            if (permission !== 'granted') {
                return { success: false, error: 'Permission denied to write to folder' };
            }

            // Create file in the books folder
            const fileHandle = await this._booksFolderHandle.getFileHandle(fileName, { create: true });
            const writable = await fileHandle.createWritable();
            await writable.write(pdfBlob);
            await writable.close();

            return { success: true, fileName: fileName };
        } catch (err) {
            return { success: false, error: err.message };
        }
    },

    // Browse, import, AND catalog in one step
    browseAndCatalog: async function(htid, title, author) {
        // First browse for the PDF
        const browseResult = await this.browsePdf(htid);
        if (!browseResult.success) {
            return browseResult;
        }

        // Then catalog it
        const catalogResult = await this.catalogPdf(htid, title, author);
        
        return {
            success: true,
            imported: true,
            cataloged: catalogResult.success,
            fileName: catalogResult.fileName || browseResult.fileName,
            catalogError: catalogResult.error
        };
    }
};

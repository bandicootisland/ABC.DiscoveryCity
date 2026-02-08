// File System Access API helpers for real filesystem access
// Handles for the two main folders
window.fileSystemAccess = {
    _handles: {
        downloads: null,
        library: null
    },

    // Pick a folder and store its handle
    pickFolder: async function (folderType) {
        try {
            // Note: We don't use startIn:'downloads' because browsers block access
            // to the actual Downloads folder as a "system" folder.
            // Users should create a subfolder like Downloads/Books
            const handle = await window.showDirectoryPicker({
                mode: 'readwrite'
            });

            this._handles[folderType] = handle;

            // Store folder name for display
            localStorage.setItem(`${folderType}FolderName`, handle.name);

            return { success: true, folderName: handle.name };
        } catch (err) {
            if (err.name === 'AbortError') {
                return { success: false, cancelled: true };
            }
            console.error('pickFolder error:', err);
            return { success: false, error: err.message };
        }
    },

    // Check if we have a stored folder name (handle won't persist, but name reminder helps)
    getStoredFolderName: function (folderType) {
        return localStorage.getItem(`${folderType}FolderName`) || null;
    },

    // Check if folder handle is currently available
    hasFolderAccess: function (folderType) {
        return this._handles[folderType] !== null;
    },

    // Read entire directory structure recursively
    readDirectoryStructure: async function (folderType, maxDepth = 5) {
        const handle = this._handles[folderType];
        if (!handle) {
            return { success: false, error: 'No folder selected. Please select a folder first.' };
        }

        try {
            // Verify permission
            const permission = await handle.queryPermission({ mode: 'readwrite' });
            if (permission !== 'granted') {
                const requested = await handle.requestPermission({ mode: 'readwrite' });
                if (requested !== 'granted') {
                    return { success: false, error: 'Permission denied' };
                }
            }

            const files = [];
            const rootPath = handle.name;

            // Add root folder
            files.push({
                id: rootPath,
                parentId: null,
                name: handle.name,
                path: rootPath,
                isDirectory: true,
                hasDirectories: false,
                size: 0,
                extension: '',
                dateCreated: new Date().toISOString(),
                dateModified: new Date().toISOString()
            });

            // Read recursively
            await this._readDirectoryRecursive(handle, rootPath, null, files, 0, maxDepth);

            // Update hasDirectories flags
            for (const file of files) {
                if (file.isDirectory) {
                    file.hasDirectories = files.some(f => f.parentId === file.id && f.isDirectory);
                }
            }

            return { success: true, files: files, rootPath: rootPath };
        } catch (err) {
            console.error('readDirectoryStructure error:', err);
            return { success: false, error: err.message };
        }
    },

    // Recursive helper to read directory contents
    _readDirectoryRecursive: async function (dirHandle, currentPath, parentId, files, depth, maxDepth) {
        if (depth >= maxDepth) return;

        try {
            for await (const entry of dirHandle.values()) {
                const entryPath = `${currentPath}/${entry.name}`;
                const entryId = entryPath;

                if (entry.kind === 'directory') {
                    files.push({
                        id: entryId,
                        parentId: parentId || currentPath,
                        name: entry.name,
                        path: entryPath,
                        isDirectory: true,
                        hasDirectories: false,
                        size: 0,
                        extension: '',
                        dateCreated: new Date().toISOString(),
                        dateModified: new Date().toISOString()
                    });

                    // Recurse into subdirectory
                    await this._readDirectoryRecursive(entry, entryPath, entryId, files, depth + 1, maxDepth);
                } else {
                    // It's a file
                    let size = 0;
                    let dateModified = new Date();
                    try {
                        const file = await entry.getFile();
                        size = file.size;
                        dateModified = new Date(file.lastModified);
                    } catch (e) {
                        // Some files may not be readable
                    }

                    const extension = entry.name.includes('.') ? 
                        '.' + entry.name.split('.').pop().toLowerCase() : '';

                    files.push({
                        id: entryId,
                        parentId: parentId || currentPath,
                        name: entry.name,
                        path: entryPath,
                        isDirectory: false,
                        hasDirectories: false,
                        size: size,
                        extension: extension,
                        dateCreated: dateModified.toISOString(),
                        dateModified: dateModified.toISOString()
                    });
                }
            }
        } catch (err) {
            console.warn(`Could not read directory ${currentPath}:`, err);
        }
    },

    // Get file contents as blob/arraybuffer for reading
    getFileBlob: async function (folderType, filePath) {
        const handle = this._handles[folderType];
        if (!handle) {
            return { success: false, error: 'No folder selected' };
        }

        try {
            // Navigate to the file
            const pathParts = filePath.split('/').filter(p => p);
            // Skip the root folder name
            const relativeParts = pathParts.slice(1);

            let currentHandle = handle;
            for (let i = 0; i < relativeParts.length - 1; i++) {
                currentHandle = await currentHandle.getDirectoryHandle(relativeParts[i]);
            }

            const fileName = relativeParts[relativeParts.length - 1];
            const fileHandle = await currentHandle.getFileHandle(fileName);
            const file = await fileHandle.getFile();

            return { success: true, file: file };
        } catch (err) {
            console.error('getFileBlob error:', err);
            return { success: false, error: err.message };
        }
    },

    // Get PDF as object URL for viewing
    getPdfUrl: async function (folderType, filePath) {
        const result = await this.getFileBlob(folderType, filePath);
        if (!result.success) {
            return result;
        }

        const url = URL.createObjectURL(result.file);
        return { success: true, url: url };
    },

    // Get PDF as base64 string for Telerik PdfViewer
    getPdfBytes: async function (folderType, filePath) {
        const result = await this.getFileBlob(folderType, filePath);
        if (!result.success) {
            console.error('getPdfBytes failed:', result.error);
            return { success: false, error: result.error };
        }

        try {
            const arrayBuffer = await result.file.arrayBuffer();
            const uint8Array = new Uint8Array(arrayBuffer);
            
            // Convert to base64 for reliable transfer to Blazor
            let binary = '';
            const chunkSize = 8192;
            for (let i = 0; i < uint8Array.length; i += chunkSize) {
                const chunk = uint8Array.subarray(i, i + chunkSize);
                binary += String.fromCharCode.apply(null, chunk);
            }
            const base64 = btoa(binary);
            
            console.log('getPdfBytes: Read', uint8Array.length, 'bytes, base64 length:', base64.length);
            return { success: true, data: base64, size: uint8Array.length };
        } catch (err) {
            console.error('getPdfBytes conversion error:', err);
            return { success: false, error: err.message };
        }
    },

    // Copy a file from one folder to another
    copyFile: async function (sourceFolderType, sourcePath, destFolderType, destFileName) {
        const sourceHandle = this._handles[sourceFolderType];
        const destHandle = this._handles[destFolderType];

        if (!sourceHandle || !destHandle) {
            return { success: false, error: 'Both folders must be selected' };
        }

        try {
            // Get source file
            const sourceResult = await this.getFileBlob(sourceFolderType, sourcePath);
            if (!sourceResult.success) {
                return sourceResult;
            }

            // Create destination file
            const destFileHandle = await destHandle.getFileHandle(destFileName, { create: true });
            const writable = await destFileHandle.createWritable();

            // Write the file
            await writable.write(sourceResult.file);
            await writable.close();

            return { success: true, fileName: destFileName };
        } catch (err) {
            console.error('copyFile error:', err);
            return { success: false, error: err.message };
        }
    },

    // Move file (copy then delete)
    moveFile: async function (sourceFolderType, sourcePath, destFolderType, destFileName) {
        const copyResult = await this.copyFile(sourceFolderType, sourcePath, destFolderType, destFileName);
        if (!copyResult.success) {
            return copyResult;
        }

        // Delete source
        const deleteResult = await this.deleteFile(sourceFolderType, sourcePath);
        if (!deleteResult.success) {
            return { success: true, warning: 'File copied but could not delete source: ' + deleteResult.error };
        }

        return { success: true, fileName: destFileName };
    },

    // Delete a file
    deleteFile: async function (folderType, filePath) {
        const handle = this._handles[folderType];
        if (!handle) {
            return { success: false, error: 'No folder selected' };
        }

        try {
            const pathParts = filePath.split('/').filter(p => p);
            const relativeParts = pathParts.slice(1);

            let currentHandle = handle;
            for (let i = 0; i < relativeParts.length - 1; i++) {
                currentHandle = await currentHandle.getDirectoryHandle(relativeParts[i]);
            }

            const fileName = relativeParts[relativeParts.length - 1];
            await currentHandle.removeEntry(fileName);

            return { success: true };
        } catch (err) {
            console.error('deleteFile error:', err);
            return { success: false, error: err.message };
        }
    },

    // Create a folder
    createFolder: async function (folderType, parentPath, folderName) {
        const handle = this._handles[folderType];
        if (!handle) {
            return { success: false, error: 'No folder selected' };
        }

        try {
            const pathParts = parentPath.split('/').filter(p => p);
            const relativeParts = pathParts.slice(1);

            let currentHandle = handle;
            for (const part of relativeParts) {
                currentHandle = await currentHandle.getDirectoryHandle(part);
            }

            await currentHandle.getDirectoryHandle(folderName, { create: true });

            return { success: true };
        } catch (err) {
            console.error('createFolder error:', err);
            return { success: false, error: err.message };
        }
    },

    // Rename a file
    renameFile: async function (folderType, oldPath, newName) {
        // File System Access API doesn't have a rename - we need to copy and delete
        const handle = this._handles[folderType];
        if (!handle) {
            return { success: false, error: 'No folder selected' };
        }

        try {
            const pathParts = oldPath.split('/').filter(p => p);
            const relativeParts = pathParts.slice(1);

            let currentHandle = handle;
            for (let i = 0; i < relativeParts.length - 1; i++) {
                currentHandle = await currentHandle.getDirectoryHandle(relativeParts[i]);
            }

            const oldFileName = relativeParts[relativeParts.length - 1];
            
            // Get old file
            const oldFileHandle = await currentHandle.getFileHandle(oldFileName);
            const oldFile = await oldFileHandle.getFile();

            // Create new file
            const newFileHandle = await currentHandle.getFileHandle(newName, { create: true });
            const writable = await newFileHandle.createWritable();
            await writable.write(oldFile);
            await writable.close();

            // Delete old file
            await currentHandle.removeEntry(oldFileName);

            return { success: true };
        } catch (err) {
            console.error('renameFile error:', err);
            return { success: false, error: err.message };
        }
    },

    // Move a file to a different folder within the same folder tree
    moveFileToFolder: async function (folderType, sourcePath, destFolderPath, fileName) {
        const handle = this._handles[folderType];
        if (!handle) {
            return { success: false, error: 'No folder selected' };
        }

        try {
            // Get source file
            const sourcePathParts = sourcePath.split('/').filter(p => p);
            const sourceRelativeParts = sourcePathParts.slice(1);

            let sourceParentHandle = handle;
            for (let i = 0; i < sourceRelativeParts.length - 1; i++) {
                sourceParentHandle = await sourceParentHandle.getDirectoryHandle(sourceRelativeParts[i]);
            }

            const sourceFileName = sourceRelativeParts[sourceRelativeParts.length - 1];
            const sourceFileHandle = await sourceParentHandle.getFileHandle(sourceFileName);
            const sourceFile = await sourceFileHandle.getFile();

            // Navigate to destination folder
            const destPathParts = destFolderPath.split('/').filter(p => p);
            const destRelativeParts = destPathParts.slice(1);

            let destHandle = handle;
            for (const part of destRelativeParts) {
                destHandle = await destHandle.getDirectoryHandle(part);
            }

            // Create file in destination
            const destFileHandle = await destHandle.getFileHandle(fileName, { create: true });
            const writable = await destFileHandle.createWritable();
            await writable.write(sourceFile);
            await writable.close();

            // Delete source file
            await sourceParentHandle.removeEntry(sourceFileName);

            // Calculate new path
            const newPath = destFolderPath + '/' + fileName;

            return { success: true, newPath: newPath };
        } catch (err) {
            console.error('moveFileToFolder error:', err);
            return { success: false, error: err.message };
        }
    },

    // Revoke an object URL when done
    revokeUrl: function (url) {
        URL.revokeObjectURL(url);
    }
};

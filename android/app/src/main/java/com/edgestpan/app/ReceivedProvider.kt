package com.edgestpan.app

import android.database.Cursor
import android.database.MatrixCursor
import android.os.CancellationSignal
import android.os.ParcelFileDescriptor
import android.provider.DocumentsContract
import android.provider.DocumentsProvider
import java.io.File

/** Lets the Files app open the folder of files received from the PC. */
class ReceivedProvider : DocumentsProvider() {
    override fun onCreate(): Boolean = true

    override fun queryRoots(projection: Array<out String>?): Cursor {
        val cursor = MatrixCursor(projection ?: arrayOf(
            DocumentsContract.Root.COLUMN_ROOT_ID,
            DocumentsContract.Root.COLUMN_DOCUMENT_ID,
            DocumentsContract.Root.COLUMN_TITLE,
            DocumentsContract.Root.COLUMN_FLAGS,
            DocumentsContract.Root.COLUMN_ICON
        ))
        cursor.newRow()
            .add(DocumentsContract.Root.COLUMN_ROOT_ID, "received")
            .add(DocumentsContract.Root.COLUMN_DOCUMENT_ID, "root")
            .add(DocumentsContract.Root.COLUMN_TITLE, "EdgeSpan")
            .add(DocumentsContract.Root.COLUMN_FLAGS, DocumentsContract.Root.FLAG_LOCAL_ONLY or DocumentsContract.Root.FLAG_SUPPORTS_IS_CHILD)
            .add(DocumentsContract.Root.COLUMN_ICON, R.mipmap.ic_launcher)
        return cursor
    }

    override fun queryDocument(documentId: String, projection: Array<out String>?): Cursor {
        val cursor = MatrixCursor(projection ?: arrayOf(
            DocumentsContract.Document.COLUMN_DOCUMENT_ID,
            DocumentsContract.Document.COLUMN_DISPLAY_NAME,
            DocumentsContract.Document.COLUMN_MIME_TYPE,
            DocumentsContract.Document.COLUMN_SIZE,
            DocumentsContract.Document.COLUMN_FLAGS
        ))
        row(cursor, documentId)
        return cursor
    }

    override fun queryChildDocuments(parentDocumentId: String, projection: Array<out String>?, sortOrder: String?): Cursor {
        val cursor = MatrixCursor(projection ?: arrayOf(
            DocumentsContract.Document.COLUMN_DOCUMENT_ID,
            DocumentsContract.Document.COLUMN_DISPLAY_NAME,
            DocumentsContract.Document.COLUMN_MIME_TYPE,
            DocumentsContract.Document.COLUMN_SIZE
        ))
        if (parentDocumentId == "root") {
            folder().listFiles()?.forEach { row(cursor, it.name) }
        }
        return cursor
    }

    override fun openDocument(documentId: String, mode: String, signal: CancellationSignal?): ParcelFileDescriptor {
        return ParcelFileDescriptor.open(File(folder(), documentId), ParcelFileDescriptor.MODE_READ_ONLY)
    }

    override fun isChildDocument(parentDocumentId: String, documentId: String): Boolean = parentDocumentId == "root" && documentId != "root"

    private fun folder(): File = ClipHub.receivedDir(context!!)

    private fun row(cursor: MatrixCursor, id: String) {
        if (id == "root") {
            cursor.newRow()
                .add(DocumentsContract.Document.COLUMN_DOCUMENT_ID, "root")
                .add(DocumentsContract.Document.COLUMN_DISPLAY_NAME, "Received")
                .add(DocumentsContract.Document.COLUMN_MIME_TYPE, DocumentsContract.Document.MIME_TYPE_DIR)
                .add(DocumentsContract.Document.COLUMN_SIZE, 0)
                .add(DocumentsContract.Document.COLUMN_FLAGS, 0)
            return
        }
        val file = File(folder(), id)
        cursor.newRow()
            .add(DocumentsContract.Document.COLUMN_DOCUMENT_ID, id)
            .add(DocumentsContract.Document.COLUMN_DISPLAY_NAME, file.name)
            .add(DocumentsContract.Document.COLUMN_MIME_TYPE, "application/octet-stream")
            .add(DocumentsContract.Document.COLUMN_SIZE, file.length())
            .add(DocumentsContract.Document.COLUMN_FLAGS, DocumentsContract.Document.FLAG_SUPPORTS_DELETE)
    }
}

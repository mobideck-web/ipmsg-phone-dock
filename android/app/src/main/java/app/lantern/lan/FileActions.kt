package app.lantern.lan

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.DocumentsContract
import android.content.ContentValues
import android.provider.MediaStore

object FileActions {
    fun capture(context: Context, video: Boolean): Uri {
        val collection = if (video) MediaStore.Video.Media.EXTERNAL_CONTENT_URI else MediaStore.Images.Media.EXTERNAL_CONTENT_URI
        return requireNotNull(context.contentResolver.insert(collection, ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME, (if (video) "Video-" else "Photo-") + System.currentTimeMillis() + (if (video) ".mp4" else ".jpg"))
            put(MediaStore.MediaColumns.MIME_TYPE, if (video) "video/mp4" else "image/jpeg")
            put(MediaStore.MediaColumns.RELATIVE_PATH, if (video) "Movies/IPMsg" else "Pictures/IPMsg")
            // The external camera has a URI grant, but Android restricts pending rows to their owner.
            put(MediaStore.MediaColumns.IS_PENDING, 0)
        })) { "Cannot create camera file" }
    }
    fun finishCapture(context: Context, uri: Uri, success: Boolean) {
        if (success) context.contentResolver.update(uri, ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }, null, null)
        else context.contentResolver.delete(uri, null, null)
    }
    fun open(model: Model, value: String, name: String) {
        val uri = Uri.parse(value)
        val ext = name.substringAfterLast('.', "").lowercase()
        val type = runCatching { model.context.contentResolver.getType(uri) }.getOrNull()?.takeUnless { it == "application/octet-stream" }
            ?: android.webkit.MimeTypeMap.getSingleton().getMimeTypeFromExtension(ext) ?: "application/octet-stream"
        runCatching { model.context.startActivity(Intent(Intent.ACTION_VIEW).setDataAndType(uri, type).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)) }
            .onFailure { model.error(IllegalStateException("File unavailable or no compatible viewer. Try Files.")) }
    }
    fun folder(model: Model, value: String) {
        val uri = Uri.parse(value)
        val parent = runCatching {
            val id = DocumentsContract.getDocumentId(uri)
            require('/' in id)
            DocumentsContract.buildDocumentUri(uri.authority, id.substringBeforeLast('/'))
        }.getOrNull() ?: runCatching {
            model.context.contentResolver.query(uri, arrayOf(MediaStore.MediaColumns.RELATIVE_PATH), null, null, null)?.use { cursor ->
                if (cursor.moveToFirst()) DocumentsContract.buildDocumentUri("com.android.externalstorage.documents", "primary:" + cursor.getString(0).trimEnd('/')) else null
            }
        }.getOrNull()
        val intent = if (parent != null) Intent(Intent.ACTION_VIEW).setDataAndType(parent, DocumentsContract.Document.MIME_TYPE_DIR)
        else Intent(Intent.ACTION_OPEN_DOCUMENT).setType("*/*").addCategory(Intent.CATEGORY_OPENABLE).putExtra(DocumentsContract.EXTRA_INITIAL_URI, uri)
        runCatching { model.context.startActivity(intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_GRANT_READ_URI_PERMISSION)) }
            .onFailure { runCatching { model.context.startActivity(Intent(Intent.ACTION_OPEN_DOCUMENT).setType("*/*").addCategory(Intent.CATEGORY_OPENABLE).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }.onFailure(model::error) }
    }
}

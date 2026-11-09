package app.lantern.lan

import android.content.ContentValues
import android.net.Uri
import android.provider.DocumentsContract
import android.provider.MediaStore
import java.net.Inet4Address
import java.net.NetworkInterface

object LocalAddresses {
    fun list(): List<String> = LanRouting.routes().map { it.address }
    fun forPeer(ip: String): String? = if (ip == "127.0.0.1") ip else LanRoute.select(LanRouting.routes(), ip)?.address
}

object DownloadStorage {
    fun label(model: Model): String = model.preferences.getString("download-folder", null)?.let {
        runCatching { DocumentsContract.getTreeDocumentId(Uri.parse(it)).substringAfter(':') }.getOrDefault("Selected folder")
    } ?: "Downloads/IPMsg"

    fun create(model: Model, filename: String, expectedSize: Long = 0): Uri {
        require(expectedSize >= 0)
        if (model.preferences.getString("download-folder", null) == null) require(android.os.StatFs(android.os.Environment.getExternalStorageDirectory().absolutePath).availableBytes - 8L * 1024 * 1024 >= expectedSize) { "Not enough storage. Free space and retry." }
        val name = Wire.safeName(filename)
        val mime = android.webkit.MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substringAfterLast('.', "").lowercase()) ?: "application/octet-stream"
        val tree = model.preferences.getString("download-folder", null)
        if (tree != null) {
            val uri = Uri.parse(tree)
            val parent = DocumentsContract.buildDocumentUriUsingTree(uri, DocumentsContract.getTreeDocumentId(uri))
            // Never replace an existing document: provider creates a unique display name.
            return requireNotNull(DocumentsContract.createDocument(model.context.contentResolver, parent, mime, name)) { "Cannot save in the chosen folder. Change it in Settings." }.also { model.repository.partial(it) }
        }
        return requireNotNull(model.context.contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME, name)
            put(MediaStore.MediaColumns.MIME_TYPE, mime)
            put(MediaStore.MediaColumns.RELATIVE_PATH, "Download/IPMsg")
            put(MediaStore.MediaColumns.IS_PENDING, 1)
        })) { "Cannot create download" }.also { model.repository.partial(it) }
    }

    fun publish(model: Model, uri: Uri, expectedSize: Long? = null) {
        model.context.contentResolver.openFileDescriptor(uri,"r")!!.use { fd -> if(expectedSize != null && fd.statSize >= 0) require(fd.statSize == expectedSize) { "Saved size does not match the offered file" } }
        if (uri.authority == MediaStore.AUTHORITY) require(model.context.contentResolver.update(uri, ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }, null, null) == 1) { "Could not finish saving the download" }
        model.repository.forgetPartial(uri)
    }
    fun cleanup(model: Model, stale: List<Uri> = model.repository.partials()) {
        stale.forEach { uri ->
            val deleted = runCatching { if(android.provider.DocumentsContract.isDocumentUri(model.context,uri)) DocumentsContract.deleteDocument(model.context.contentResolver,uri) else model.context.contentResolver.delete(uri,null,null) > 0 }.getOrDefault(false)
            if(deleted) model.repository.forgetPartial(uri)
        }
    }
}

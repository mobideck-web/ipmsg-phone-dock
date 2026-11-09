package app.lantern.lan

import android.app.Activity
import android.content.Intent
import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.os.Bundle
import android.view.DragEvent
import android.view.Gravity
import android.view.WindowManager
import android.widget.TextView
import java.util.UUID

/** Normal narrow Activity, not an overlay or an accessibility service. */
class DockEdgeActivity:Activity() {
    private val owner="edge-${UUID.randomUUID()}"
    override fun onCreate(savedInstanceState:Bundle?) {
        super.onCreate(savedInstanceState)
        val token=intent.getStringExtra("dock_session");intent.removeExtra("dock_session")
        if(!DockSessions.matches(token)) {finish();return}
        window.setBackgroundDrawableResource(android.R.color.transparent)
        window.clearFlags(WindowManager.LayoutParams.FLAG_DIM_BEHIND)
        window.addFlags(WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE)
        val label=TextView(this).apply {
            text="IPMsg Dock\n\nDrop a file here\n\nThen drag it from the PC shelf\n\nTap to close"
            gravity=Gravity.CENTER;setTextColor(Color.WHITE);textSize=13f;setPadding(10,24,10,24)
            background=GradientDrawable().apply{setColor(Color.argb(225,25,42,48));cornerRadius=24f}
            setOnClickListener{finish()}
        }
        setContentView(label)
        window.setLayout((108*resources.displayMetrics.density).toInt(),WindowManager.LayoutParams.MATCH_PARENT)
        window.setGravity(Gravity.END)
        label.setOnDragListener {_,event -> when(event.action) {
            DragEvent.ACTION_DRAG_STARTED -> {label.alpha=0.85f;true}
            DragEvent.ACTION_DRAG_ENTERED -> {label.alpha=1f;true}
            DragEvent.ACTION_DROP -> {
                val permission=requestDragAndDropPermissions(event)
                val clip=event.clipData
                val uris=clip?.let{(0 until minOf(it.itemCount,31)).mapNotNull{index->it.getItemAt(index).uri}}.orEmpty()
                if(permission!=null && uris.isNotEmpty()) {
                    DockSessions.import(this,uris,owner){permission.release()}
                    label.text="Adding selection…\n\nCheck the PC shelf for ready files\n\nTap to close"
                } else {
                    permission?.release()
                    label.text="This app shared text, a link, or no readable file.\n\nUse Share → IPMsg Dock for the actual file.\n\nTap to close"
                };true
            }
            DragEvent.ACTION_DRAG_ENDED -> {label.alpha=1f;true}
            else -> true
        } }
    }
    override fun onDestroy() {DockSessions.revokeOwner(owner);super.onDestroy()}
}

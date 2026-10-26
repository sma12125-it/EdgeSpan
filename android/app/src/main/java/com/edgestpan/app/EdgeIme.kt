package com.edgestpan.app

import android.inputmethodservice.InputMethodService
import android.view.View
import android.widget.LinearLayout

class EdgeIme : InputMethodService() {
    override fun onCreateInputView(): View {
        Input.current = this
        return LinearLayout(this)
    }

    override fun onStartInputView(info: android.view.inputmethod.EditorInfo?, restarting: Boolean) {
        Input.current = this
        ClipHub.readIfAllowed(this)
        super.onStartInputView(info, restarting)
    }

    override fun onDestroy() {
        if (Input.current === this) Input.current = null
        super.onDestroy()
    }
}

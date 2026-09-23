package com.astralpath.app

import android.annotation.SuppressLint
import android.app.Activity
import android.os.Bundle
import android.webkit.WebChromeClient
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.Toast
import androidx.webkit.WebViewAssetLoader
import java.io.BufferedReader

/** 与 Web / Windows 同一 UI：内嵌 www/index.html + 本机 REST */
class MainActivity : Activity() {
    private lateinit var web: WebView
    private lateinit var assetLoader: WebViewAssetLoader
    private val apiBase = "http://127.0.0.1:5190/"

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        web = WebView(this)
        setContentView(web)
        assetLoader = WebViewAssetLoader.Builder()
            .addPathHandler("/assets/", WebViewAssetLoader.AssetsPathHandler(this))
            .build()
        web.settings.apply {
            javaScriptEnabled = true
            domStorageEnabled = true
            allowFileAccess = true
            useWideViewPort = true
            loadWithOverviewMode = true
            mixedContentMode = WebSettings.MIXED_CONTENT_ALWAYS_ALLOW
            textZoom = 100
        }
        web.webChromeClient = object : WebChromeClient() {
            override fun onJsAlert(view: WebView?, url: String?, message: String?, result: android.webkit.JsResult?): Boolean {
                Toast.makeText(this@MainActivity, message ?: "", Toast.LENGTH_SHORT).show()
                result?.confirm()
                return true
            }
        }
        web.webViewClient = object : WebViewClient() {
            override fun shouldInterceptRequest(view: WebView?, request: WebResourceRequest?): WebResourceResponse? {
                val uri = request?.url ?: return null
                return if (uri.host == "appassets.androidplatform.net") assetLoader.shouldInterceptRequest(uri)
                else super.shouldInterceptRequest(view, request)
            }
        }
        val html = assets.open("www/index.html").bufferedReader().use(BufferedReader::readText)
        web.loadDataWithBaseURL(apiBase, html, "text/html", "utf-8", null)
    }
}

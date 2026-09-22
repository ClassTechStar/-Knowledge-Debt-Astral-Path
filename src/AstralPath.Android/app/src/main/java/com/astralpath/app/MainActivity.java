package com.astralpath.app;

import android.annotation.SuppressLint;
import android.app.Activity;
import android.os.Bundle;
import android.util.Log;
import android.view.KeyEvent;
import android.view.View;
import android.view.WindowManager;
import android.webkit.ConsoleMessage;
import android.webkit.WebChromeClient;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;

import java.io.BufferedReader;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.nio.charset.StandardCharsets;

/**
 * 知债：星穹学途 · Android 原生壳 + 手机同源业务 UI
 *
 * <p>加载 {@code www/mobile.html}：底部标签导航、全功能（藏书阁/识网/知债/今日/智能体/画像/账户），
 * 业务接口与 Windows / Web 完全一致（同一 AstralPath.Api）。</p>
 */
public class MainActivity extends Activity {

    private static final String TAG = "AstralPath";
    public static final String API_BASE = "http://127.0.0.1:5190/";

    private WebView web;

    @SuppressLint("SetJavaScriptEnabled")
    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        // 浅色系统栏 + 尽量铺满（黑边在 Java 侧处理，避免主题资源缺失）
        getWindow().setStatusBarColor(0xFFF3F4F5);
        getWindow().setNavigationBarColor(0xFFF3F4F5);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_DRAWS_SYSTEM_BAR_BACKGROUNDS);
        try {
            getWindow().getDecorView().setSystemUiVisibility(
                    View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                            | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                            | View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR
                            | View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR);
        } catch (Throwable ignored) { }

        setContentView(R.layout.activity_main);
        web = findViewById(R.id.web);
        // WebView 铺满，避免嵌套滚动容器
        if (web.getParent() instanceof android.view.ViewGroup) {
            android.view.ViewGroup p = (android.view.ViewGroup) web.getParent();
            android.view.ViewGroup.LayoutParams lp = web.getLayoutParams();
            lp.width = android.view.ViewGroup.LayoutParams.MATCH_PARENT;
            lp.height = android.view.ViewGroup.LayoutParams.MATCH_PARENT;
            web.setLayoutParams(lp);
        }

        WebSettings s = web.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);
        s.setAllowFileAccess(true);
        s.setAllowContentAccess(true);
        s.setUseWideViewPort(false);
        s.setLoadWithOverviewMode(false);
        s.setSupportZoom(false);
        s.setBuiltInZoomControls(false);
        s.setTextZoom(100);
        s.setCacheMode(WebSettings.LOAD_DEFAULT);
        s.setMediaPlaybackRequiresUserGesture(false);
        s.setBlockNetworkImage(false);
        s.setMixedContentMode(WebSettings.MIXED_CONTENT_ALWAYS_ALLOW);
        // 流畅：整页用默认合成，避免超大硬件层；隐藏滚动条
        web.setLayerType(View.LAYER_TYPE_NONE, null);
        web.setOverScrollMode(View.OVER_SCROLL_NEVER);
        web.setHorizontalScrollBarEnabled(false);
        web.setVerticalScrollBarEnabled(false);
        web.setBackgroundColor(0xFFF3F4F5);

        web.setWebChromeClient(new WebChromeClient() {
            @Override
            public boolean onConsoleMessage(ConsoleMessage cm) {
                Log.d(TAG, cm.message());
                return true;
            }
        });
        web.setWebViewClient(new WebViewClient() {
            @Override
            public void onPageFinished(WebView view, String url) {
                view.evaluateJavascript(
                        "window.__ASTRALPATH_API__=\"" + API_BASE.substring(0, API_BASE.length() - 1) + "\";"
                                + "window.__ASTRALPATH_ANDROID__=true;", null);
            }
        });

        String html = readAssetText("www/mobile.html");
        if (html == null) {
            web.loadDataWithBaseURL(null, "<h3>缺少 mobile.html</h3>", "text/html", "utf-8", null);
            return;
        }
        web.loadDataWithBaseURL(API_BASE, html, "text/html", "utf-8", null);
    }

    private String readAssetText(String path) {
        try (InputStream in = getAssets().open(path);
             BufferedReader br = new BufferedReader(new InputStreamReader(in, StandardCharsets.UTF_8))) {
            StringBuilder sb = new StringBuilder();
            String line;
            while ((line = br.readLine()) != null) sb.append(line).append('\n');
            return sb.toString();
        } catch (Exception e) {
            Log.e(TAG, "readAsset", e);
            return null;
        }
    }

    @Override
    public boolean onKeyDown(int keyCode, KeyEvent event) {
        if (keyCode == KeyEvent.KEYCODE_BACK && web != null && web.canGoBack()) {
            web.goBack();
            return true;
        }
        return super.onKeyDown(keyCode, event);
    }

    @Override
    protected void onDestroy() {
        if (web != null) web.destroy();
        super.onDestroy();
    }
}

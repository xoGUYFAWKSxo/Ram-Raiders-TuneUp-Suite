/**
 * Ram Raiders TuneUp — Waitlist Frontend Controller
 * Build: v6 — Restored password lock with Ctrl+Shift+D trigger
 */

document.addEventListener('DOMContentLoaded', () => {
    // ----------------------------------------------------
    // Waitlist Seats Counter Logic
    // ----------------------------------------------------
    const seatsLimit = 10000;
    const progressFill = document.getElementById('progress-fill');
    const seatsTakenVal = document.getElementById('seats-taken-val');
    const seatsRemainingVal = document.getElementById('seats-remaining-val');

    function updateSeatsUI(animate = true) {
        const seatsTaken = WaitlistDB.getSeatsCount();
        const seatsRemaining = seatsLimit - seatsTaken;
        const fillPercentage = (seatsTaken / seatsLimit) * 100;

        if (seatsTakenVal) seatsTakenVal.textContent = seatsTaken.toLocaleString();
        if (seatsRemainingVal) seatsRemainingVal.textContent = seatsRemaining.toLocaleString();
        
        if (progressFill) {
            progressFill.style.width = `${fillPercentage}%`;
        }
    }

    // Initialize UI
    updateSeatsUI(false);

    // Dynamic, simulated ticker representing other organic signups to create urgency
    setInterval(() => {
        const currentCount = WaitlistDB.getSeatsCount();
        if (currentCount < seatsLimit && Math.random() > 0.7) { // 30% chance every 20s
            const increment = Math.floor(Math.random() * 2) + 1;
            const newCount = Math.min(seatsLimit, currentCount + increment);
            localStorage.setItem('ram_raiders_waitlist_v2_seats_count', newCount.toString());
            updateSeatsUI(true);
            
            // Re-render admin stats if drawer is open
            if (document.getElementById('dev-drawer').classList.contains('open')) {
                renderAdminConsole();
            }
        }
    }, 20000);


    // ----------------------------------------------------
    // Multi-Step Survey Wizard Controller
    // ----------------------------------------------------
    const wizardSlides = document.querySelectorAll('.wizard-slide');
    const stepNodes = document.querySelectorAll('.step-node');
    const btnPrev = document.getElementById('btn-prev');
    const btnNext = document.getElementById('btn-next');
    
    let currentStep = 1;
    const surveyAnswers = {
        q1: '',
        q2: '',
        q3: '',
        contact: ''
    };

    // Card Selection Events (Step 1, 2, 3)
    document.querySelectorAll('.wizard-slide').forEach((slide, slideIndex) => {
        const stepNum = slideIndex + 1;
        if (stepNum > 3) return; // Only process survey steps

        const options = slide.querySelectorAll('.option-card');
        options.forEach(option => {
            option.addEventListener('click', () => {
                // Remove selected from siblings
                options.forEach(o => o.classList.remove('selected'));
                // Add selected to clicked
                option.classList.add('selected');
                
                const val = option.getAttribute('data-value');
                surveyAnswers[`q${stepNum}`] = val;
                
                // Automatically progress to next step after a tiny delay for rich feel
                setTimeout(() => {
                    navigateWizard(1);
                }, 350);
            });
        });
    });

    // Form inputs change (Step 4)
    const contactInput = document.getElementById('contact-input');
    const privacyCheck = document.getElementById('privacy-check');
    if (contactInput) {
        contactInput.addEventListener('input', () => {
            surveyAnswers.contact = contactInput.value;
            validateStep4();
        });
    }
    if (privacyCheck) {
        privacyCheck.addEventListener('change', () => {
            validateStep4();
        });
    }

    function validateStep4() {
        const value = (contactInput ? contactInput.value.trim() : '');
        const isPrivacyChecked = privacyCheck ? privacyCheck.checked : false;
        
        let isValid = false;
        if (value.length > 0 && isPrivacyChecked) {
            const isEmail = /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value);
            const isPhone = /^[\d\s()+-]{7,20}$/.test(value);
            isValid = isEmail || isPhone;
        }

        if (btnNext && currentStep === 4) {
            btnNext.disabled = !isValid;
        }
        return isValid;
    }

    // Main Wizard Navigator
    function goToStep(stepNumber) {
        if (stepNumber < 1 || stepNumber > 5) return;
        
        // Hide all slides with transitions
        wizardSlides.forEach((slide, idx) => {
            const slideStep = idx + 1;
            slide.classList.remove('active', 'prev-exit');
            if (slideStep < stepNumber) {
                slide.classList.add('prev-exit');
            }
        });

        // Set active slide
        const targetSlide = document.querySelector(`.wizard-slide[data-step="${stepNumber}"]`);
        if (targetSlide) {
            targetSlide.classList.add('active');
        }

        currentStep = stepNumber;

        // Update indicator nodes (Steps 1 to 4)
        stepNodes.forEach((node, idx) => {
            const nodeStep = idx + 1;
            node.classList.remove('active', 'completed');
            if (nodeStep === currentStep) {
                node.classList.add('active');
            } else if (nodeStep < currentStep) {
                node.classList.add('completed');
                node.innerHTML = '✓'; // Complete checkmark
            } else {
                node.innerHTML = nodeStep; // Reset number
            }
        });

        // Adjust Nav Buttons
        if (btnPrev) {
            btnPrev.style.visibility = (currentStep === 1 || currentStep >= 5) ? 'hidden' : 'visible';
        }
        
        if (btnNext) {
            // Success step does not show next button
            if (currentStep >= 5) {
                btnNext.style.display = 'none';
            } else {
                btnNext.style.display = 'inline-flex';
                
                // Customize labels
                if (currentStep === 4) {
                    btnNext.innerHTML = 'Complete Reservation';
                    btnNext.disabled = !validateStep4();
                } else {
                    btnNext.innerHTML = 'Next Question';
                    // Disable next if question is unanswered
                    const currentAnswer = surveyAnswers[`q${currentStep}`];
                    btnNext.disabled = !currentAnswer;
                }
            }
        }
    }

    function navigateWizard(direction) {
        const targetStep = currentStep + direction;
        
        if (direction === 1 && currentStep === 4) {
            // Form submission phase
            submitReservation();
            return;
        }

        goToStep(targetStep);
    }

    // Nav Button Click Handlers
    if (btnPrev) {
        btnPrev.addEventListener('click', () => navigateWizard(-1));
    }
    if (btnNext) {
        btnNext.addEventListener('click', () => navigateWizard(1));
    }

    // Submit Waitlist Reservation
    async function submitReservation() {
        if (!validateStep4()) return;
        
        // Visual Loading State on Button
        if (btnNext) {
            btnNext.disabled = true;
            btnNext.innerHTML = `<span style="display:inline-block; width:14px; height:14px; border:2px solid #FFF; border-top-color:transparent; border-radius:50%; animation:pulse 1s linear infinite; margin-right:8px;"></span>Securing spot...`;
        }

        try {
            const response = await WaitlistDB.registerUser(
                surveyAnswers.contact,
                surveyAnswers.q1,
                surveyAnswers.q2,
                surveyAnswers.q3
            );

            // Increment UI Seats
            updateSeatsUI(true);

            // Populate Success Screen details
            const userPosVal = document.getElementById('user-position-value');
            if (userPosVal) {
                userPosVal.textContent = response.record.waitlistPosition.toLocaleString();
            }

            // Transition to Success Step (Step 5)
            setTimeout(() => {
                goToStep(5);
            }, 1000);

        } catch (error) {
            console.error("Submission failed", error);
            alert("Oops! There was an issue registering your waitlist spot. Please try again.");
            if (btnNext) {
                btnNext.disabled = false;
                btnNext.innerHTML = 'Complete Reservation';
            }
        }
    }


    // ----------------------------------------------------
    // Screenshot Carousel / Gallery Controller
    // ----------------------------------------------------
    const galleryTrack = document.getElementById('gallery-track');
    const dots = document.querySelectorAll('.gallery-dot');
    const arrowPrev = document.getElementById('arrow-prev');
    const arrowNext = document.getElementById('arrow-next');
    
    let activeIdx = 0;
    const slideCount = dots.length;
    let carouselInterval;

    function moveGallery(index) {
        if (index < 0) index = slideCount - 1;
        if (index >= slideCount) index = 0;
        
        activeIdx = index;

        if (galleryTrack) {
            galleryTrack.style.transform = `translateX(-${activeIdx * 25}%)`;
        }

        dots.forEach((dot, idx) => {
            dot.classList.toggle('active', idx === activeIdx);
        });
    }

    function startAutoCycle() {
        carouselInterval = setInterval(() => {
            moveGallery(activeIdx + 1);
        }, 8000); // Shift every 8 seconds
    }

    function resetAutoCycle() {
        clearInterval(carouselInterval);
        startAutoCycle();
    }

    dots.forEach((dot, idx) => {
        dot.addEventListener('click', () => {
            moveGallery(idx);
            resetAutoCycle();
        });
    });

    if (arrowPrev) {
        arrowPrev.addEventListener('click', () => {
            moveGallery(activeIdx - 1);
            resetAutoCycle();
        });
    }

    if (arrowNext) {
        arrowNext.addEventListener('click', () => {
            moveGallery(activeIdx + 1);
            resetAutoCycle();
        });
    }

    // Hover pauses automatic sliding
    const galleryViewport = document.querySelector('.gallery-viewport');
    if (galleryViewport) {
        galleryViewport.addEventListener('mouseenter', () => clearInterval(carouselInterval));
        galleryViewport.addEventListener('mouseleave', startAutoCycle);
    }

    startAutoCycle();


    // ----------------------------------------------------
    // Developer / Admin Panel Drawer Controller
    // ----------------------------------------------------
    const devDrawer = document.getElementById('dev-drawer');
    const drawerBackdrop = document.getElementById('drawer-backdrop');
    const btnOpenDev = document.getElementById('dev-trigger');
    const appLogo = document.getElementById('app-logo-trigger');
    const btnCloseDev = document.getElementById('close-dev');
    const btnExportCsv = document.getElementById('btn-export-csv');
    const btnResetDb = document.getElementById('btn-reset-db');
    const dbSearchInput = document.getElementById('db-search');

    function openDevPanel() {
        const password = prompt("Enter Developer Access Password:");
        if (password === "Password4321") {
            if (devDrawer && drawerBackdrop) {
                devDrawer.classList.add('open');
                drawerBackdrop.classList.add('open');
                renderAdminConsole();
            }
        } else if (password !== null) {
            alert("Access Denied: Invalid Password.");
        }
    }

    function closeDevPanel() {
        if (devDrawer && drawerBackdrop) {
            devDrawer.classList.remove('open');
            drawerBackdrop.classList.remove('open');
        }
    }

    if (btnOpenDev) btnOpenDev.addEventListener('click', openDevPanel);
    if (btnCloseDev) btnCloseDev.addEventListener('click', closeDevPanel);
    if (drawerBackdrop) drawerBackdrop.addEventListener('click', closeDevPanel);

    // Keyboard shortcut: Ctrl + Shift + D opens the developer console
    document.addEventListener('keydown', (e) => {
        if (e.ctrlKey && e.shiftKey && e.key === 'D') {
            e.preventDefault();
            if (devDrawer && devDrawer.classList.contains('open')) {
                closeDevPanel();
            } else {
                openDevPanel();
            }
        }
    });

    // Fun easter egg: double-clicking logo also opens admin console
    if (appLogo) {
        appLogo.addEventListener('dblclick', (e) => {
            e.preventDefault();
            openDevPanel();
        });
        appLogo.style.cursor = 'pointer';
        appLogo.setAttribute('title', 'Double-click to open Admin DB Console');
    }

    // CSV Download
    if (btnExportCsv) {
        btnExportCsv.addEventListener('click', () => {
            const exported = WaitlistDB.exportToCSV();
            if (!exported) {
                alert("No registrations available to export.");
            }
        });
    }

    // Data wipe
    if (btnResetDb) {
        btnResetDb.addEventListener('click', () => {
            if (confirm("Are you sure you want to restore the waitlist database to its standard mock defaults? This will erase new submissions.")) {
                WaitlistDB.resetDB();
                updateSeatsUI(false);
                renderAdminConsole();
            }
        });
    }

    // Live search filters
    if (dbSearchInput) {
        dbSearchInput.addEventListener('input', () => {
            renderAdminConsole(dbSearchInput.value);
        });
    }

    // Core Admin Console Renderer
    function renderAdminConsole(filterQuery = '') {
        const signups = WaitlistDB.getSignups();
        const stats = WaitlistDB.getStats();
        
        // Render Stats values
        const statTotalVal = document.getElementById('stat-total-val');
        const statSeatsVal = document.getElementById('stat-seats-val');
        
        if (statTotalVal) statTotalVal.textContent = stats.total.toLocaleString();
        if (statSeatsVal) statSeatsVal.textContent = WaitlistDB.getSeatsCount().toLocaleString();

        // Render Q1 Chart
        const chartQ1 = document.getElementById('chart-q1');
        if (chartQ1) {
            chartQ1.innerHTML = renderStatDistribution(stats.q1, stats.total);
        }

        // Render Q2 Chart
        const chartQ2 = document.getElementById('chart-q2');
        if (chartQ2) {
            chartQ2.innerHTML = renderStatDistribution(stats.q2, stats.total);
        }

        // Render Q3 Chart
        const chartQ3 = document.getElementById('chart-q3');
        if (chartQ3) {
            chartQ3.innerHTML = renderStatDistribution(stats.q3, stats.total);
        }

        // Render Records List
        const recordsList = document.getElementById('records-list');
        if (!recordsList) return;

        recordsList.innerHTML = '';
        
        const filteredSignups = signups.filter(item => {
            return item.emailOrPhone.toLowerCase().includes(filterQuery.toLowerCase());
        }).reverse(); // Latest signups first

        if (filteredSignups.length === 0) {
            recordsList.innerHTML = `<div class="no-records">No signups found matching "${filterQuery}"</div>`;
            return;
        }

        filteredSignups.forEach(item => {
            const itemCard = document.createElement('div');
            itemCard.className = 'record-item';
            
            const localTime = new Date(item.timestamp).toLocaleString();
            
            itemCard.innerHTML = `
                <div class="record-summary">
                    <span class="email">${item.emailOrPhone}</span>
                    <span class="phone">Pos: #${item.waitlistPosition}</span>
                    <span class="expand-indicator">▼</span>
                </div>
                <div class="record-details">
                    <div class="detail-row">
                        <div class="detail-q">Waitlist Secured At</div>
                        <div class="detail-a" style="color: var(--text-muted);">${localTime}</div>
                    </div>
                    <div class="detail-row">
                        <div class="detail-q">Q1: Value Buy-to-Own Software</div>
                        <div class="detail-a">${item.q1}</div>
                    </div>
                    <div class="detail-row">
                        <div class="detail-q">Q2: Impact on Computing Experience</div>
                        <div class="detail-a">${item.q2}</div>
                    </div>
                    <div class="detail-row">
                        <div class="detail-q">Q3: Ecosystem Bloatware vs. Upgrade</div>
                        <div class="detail-a">${item.q3}</div>
                    </div>
                    <div class="detail-row" style="margin-top: 1rem; text-align: right;">
                        <button class="btn btn-outline" style="padding: 0.3rem 0.7rem; font-size: 0.75rem; border-color: rgba(255, 74, 74, 0.4); color: var(--accent-red); background: transparent;" onclick="deleteRecord('${item.emailOrPhone}')">Delete Entry</button>
                    </div>
                </div>
            `;

            // Expand/collapse logic
            const summary = itemCard.querySelector('.record-summary');
            summary.addEventListener('click', () => {
                const isExpanded = itemCard.classList.contains('expanded');
                
                // Collapse all others
                document.querySelectorAll('.record-item').forEach(card => card.classList.remove('expanded'));
                
                if (!isExpanded) {
                    itemCard.classList.add('expanded');
                }
            });

            recordsList.appendChild(itemCard);
        });
    }

    // Helper to render bars inside statistics aggregates
    function renderStatDistribution(qStats, total) {
        if (total === 0) return `<div class="no-records">No survey data yet</div>`;
        
        let html = '<div style="display:flex; flex-direction:column; gap: 0.75rem; margin-top:0.5rem;">';
        
        Object.entries(qStats).forEach(([opt, count]) => {
            const pct = ((count / total) * 100).toFixed(0);
            html += `
                <div>
                    <div style="display:flex; justify-content:space-between; font-size:0.75rem; margin-bottom:0.25rem;">
                        <span style="color:var(--text-main); max-width:80%; overflow:hidden; text-overflow:ellipsis; white-space:nowrap;" title="${opt}">${opt}</span>
                        <span style="color:var(--accent-cyan); font-weight:600;">${pct}% (${count})</span>
                    </div>
                    <div style="width:100%; height:6px; background:#1C1C26; border-radius:4px; overflow:hidden;">
                        <div style="width:${pct}%; height:100%; background:var(--grad-neon); border-radius:4px; box-shadow:0 0 8px rgba(0, 240, 255, 0.3);"></div>
                    </div>
                </div>
            `;
        });
        
        html += '</div>';
        return html;
    }

    // Expose delete method globally so inline onclick handlers can reach it
    window.deleteRecord = function(emailOrPhone) {
        if (confirm(`Remove waitlist entry for "${emailOrPhone}"?`)) {
            WaitlistDB.deleteSignup(emailOrPhone);
            renderAdminConsole();
        }
    };
});

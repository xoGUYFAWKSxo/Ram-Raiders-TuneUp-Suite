/**
 * Ram Raiders TuneUp — Waitlist Database Controller
 * 
 * This module manages all waitlist operations. It uses a provider-based architecture:
 * 1. "local" (default): Saves data securely inside the browser's localStorage. Completely self-contained, 
 *    works immediately on any machine or static hosting server.
 * 2. "supabase": Direct connection to a Supabase REST endpoint via fetch (requires table 'waitlist').
 * 3. "firebase": Direct connection to a Firebase Realtime Database REST endpoint via fetch.
 */

const DB_CONFIG = {
    // Active Database Provider: 'local' | 'supabase' | 'firebase'
    provider: 'local',
    
    // Supabase Credentials (Only needed if provider: 'supabase')
    supabase: {
        url: '', // Example: 'https://xyz.supabase.co'
        anonKey: '' // Your anonymous API key
    },
    
    // Firebase Credentials (Only needed if provider: 'firebase')
    firebase: {
        databaseUrl: '' // Example: 'https://project-id-default-rtdb.firebaseio.com/waitlist.json'
    }
};

// Key used for localStorage
const LOCAL_STORAGE_KEY = 'ram_raiders_waitlist_v2_signups';
const LOCAL_SEATS_KEY = 'ram_raiders_waitlist_v2_seats_count';
const DEFAULT_LIMIT = 10000;
const INITIAL_SEATS_TAKEN = 6014; // Out of 10000, ~60% filled

// Mock data to pre-populate the Developer Console on first load (for visualization)
const MOCK_SIGNUPS = [
    {
        emailOrPhone: "alex.gamertweaks@gmail.com",
        q1: "$30 - $49",
        q2: "Significantly improve gaming frame-rates and input latency",
        q3: "A major improvement — most tuners are bloated web-tech or adware, we need lightweight native tools!",
        timestamp: new Date(Date.now() - 4 * 3600 * 1000).toISOString(), // 4 hrs ago
        waitlistPosition: 9138
    },
    {
        emailOrPhone: "+1 (555) 987-6543",
        q1: "$20 - $29",
        q2: "Make my older hardware feel snappy and usable again",
        q3: "A major improvement — most tuners are bloated web-tech or adware, we need lightweight native tools!",
        timestamp: new Date(Date.now() - 3.2 * 3600 * 1000).toISOString(), // 3.2 hrs ago
        waitlistPosition: 9139
    },
    {
        emailOrPhone: "sarah_codes@outlook.com",
        q1: "$50+",
        q2: "Streamline my developer/creator productivity workflows",
        q3: "A major improvement — most tuners are bloated web-tech or adware, we need lightweight native tools!",
        timestamp: new Date(Date.now() - 2.5 * 3600 * 1000).toISOString(),
        waitlistPosition: 9140
    },
    {
        emailOrPhone: "bloatbuster_dev@yahoo.com",
        q1: "Nothing / I only use free software",
        q2: "Make my older hardware feel snappy and usable again",
        q3: "Neutral — it depends on the performance gains and utility",
        timestamp: new Date(Date.now() - 1.1 * 3600 * 1000).toISOString(),
        waitlistPosition: 9141
    },
    {
        emailOrPhone: "+1 (202) 555-0144",
        q1: "$10 - $19",
        q2: "Significantly improve gaming frame-rates and input latency",
        q3: "A major improvement — most tuners are bloated web-tech or adware, we need lightweight native tools!",
        timestamp: new Date(Date.now() - 18 * 60 * 1000).toISOString(), // 18 mins ago
        waitlistPosition: 9142
    }
];

class WaitlistDB {
    /**
     * Initializes database storage. Pre-populates mock data if local storage is empty.
     */
    static init() {
        if (DB_CONFIG.provider === 'local') {
            if (!localStorage.getItem(LOCAL_STORAGE_KEY)) {
                localStorage.setItem(LOCAL_STORAGE_KEY, JSON.stringify(MOCK_SIGNUPS));
            }
            if (!localStorage.getItem(LOCAL_SEATS_KEY)) {
                localStorage.setItem(LOCAL_SEATS_KEY, INITIAL_SEATS_TAKEN.toString());
            }
        }
    }

    /**
     * Returns the current total count of seats taken on the waitlist.
     */
    static getSeatsCount() {
        if (DB_CONFIG.provider === 'local') {
            this.init();
            return parseInt(localStorage.getItem(LOCAL_SEATS_KEY)) || INITIAL_SEATS_TAKEN;
        }
        // In external DB modes, we'd fetch the row count. For simplicity, we fall back to localStorage/mock.
        return parseInt(localStorage.getItem(LOCAL_SEATS_KEY)) || INITIAL_SEATS_TAKEN;
    }

    /**
     * Records a new user waitlist registration.
     * @param {string} emailOrPhone User's email or phone number
     * @param {string} q1 Survey Answer 1 (Pricing model preference)
     * @param {string} q2 Survey Answer 2 (Computing Experience impact)
     * @param {string} q3 Survey Answer 3 (Improvement vs Bloatware opinion)
     */
    static async registerUser(emailOrPhone, q1, q2, q3) {
        const timestamp = new Date().toISOString();
        const currentSeats = this.getSeatsCount();
        const nextSeats = Math.min(DEFAULT_LIMIT, currentSeats + 1);
        
        const record = {
            emailOrPhone: emailOrPhone.trim(),
            q1,
            q2,
            q3,
            timestamp,
            waitlistPosition: nextSeats
        };

        if (DB_CONFIG.provider === 'local') {
            this.init();
            const list = JSON.parse(localStorage.getItem(LOCAL_STORAGE_KEY)) || [];
            
            // Avoid duplicate email or phone entries
            const duplicate = list.find(u => u.emailOrPhone.toLowerCase() === emailOrPhone.trim().toLowerCase());
            if (duplicate) {
                return { success: true, alreadyExists: true, record: duplicate };
            }

            list.push(record);
            localStorage.setItem(LOCAL_STORAGE_KEY, JSON.stringify(list));
            localStorage.setItem(LOCAL_SEATS_KEY, nextSeats.toString());
            
            return { success: true, alreadyExists: false, record };
        } 
        
        else if (DB_CONFIG.provider === 'supabase') {
            if (!DB_CONFIG.supabase.url || !DB_CONFIG.supabase.anonKey) {
                throw new Error("Supabase credentials are not configured in database.js.");
            }
            try {
                // Perform a POST request directly to the Supabase REST API
                const response = await fetch(`${DB_CONFIG.supabase.url}/rest/v1/waitlist`, {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'apikey': DB_CONFIG.supabase.anonKey,
                        'Authorization': `Bearer ${DB_CONFIG.supabase.anonKey}`,
                        'Prefer': 'return=representation'
                    },
                    body: JSON.stringify({
                        email_or_phone: record.emailOrPhone,
                        q1: record.q1,
                        q2: record.q2,
                        q3: record.q3,
                        timestamp: record.timestamp,
                        waitlist_position: record.waitlistPosition
                    })
                });

                if (!response.ok) {
                    const err = await response.json();
                    throw new Error(err.message || 'Supabase error.');
                }
                
                // Track locally as well for admin interface reference
                this.saveLocalCopy(record, nextSeats);
                return { success: true, alreadyExists: false, record };
            } catch (error) {
                console.error("Supabase Save Error:", error);
                throw error;
            }
        } 
        
        else if (DB_CONFIG.provider === 'firebase') {
            if (!DB_CONFIG.firebase.databaseUrl) {
                throw new Error("Firebase databaseUrl is not configured in database.js.");
            }
            try {
                // Post to Firebase Database endpoint
                const response = await fetch(DB_CONFIG.firebase.databaseUrl, {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify(record)
                });

                if (!response.ok) {
                    throw new Error('Firebase Database error.');
                }

                this.saveLocalCopy(record, nextSeats);
                return { success: true, alreadyExists: false, record };
            } catch (error) {
                console.error("Firebase Save Error:", error);
                throw error;
            }
        }
    }

    /**
     * Helper to update the local copy of the registry when an external provider completes a write.
     */
    static saveLocalCopy(record, nextSeats) {
        this.init();
        const list = JSON.parse(localStorage.getItem(LOCAL_STORAGE_KEY)) || [];
        list.push(record);
        localStorage.setItem(LOCAL_STORAGE_KEY, JSON.stringify(list));
        localStorage.setItem(LOCAL_SEATS_KEY, nextSeats.toString());
    }

    /**
     * Returns all records from the database.
     */
    static getSignups() {
        this.init();
        return JSON.parse(localStorage.getItem(LOCAL_STORAGE_KEY)) || [];
    }

    /**
     * Deletes a signup record.
     */
    static deleteSignup(emailOrPhone) {
        this.init();
        let list = JSON.parse(localStorage.getItem(LOCAL_STORAGE_KEY)) || [];
        list = list.filter(item => item.emailOrPhone !== emailOrPhone);
        localStorage.setItem(LOCAL_STORAGE_KEY, JSON.stringify(list));
        return true;
    }

    /**
     * Completely resets all database data to standard mocks.
     */
    static resetDB() {
        localStorage.removeItem(LOCAL_STORAGE_KEY);
        localStorage.removeItem(LOCAL_SEATS_KEY);
        this.init();
        return true;
    }

    /**
     * Computes statistics from waitlist records to show analytical aggregates in the admin drawer.
     */
    static getStats() {
        const signups = this.getSignups();
        const stats = {
            total: signups.length,
            q1: {},
            q2: {},
            q3: {}
        };

        signups.forEach(item => {
            // Stats Q1
            stats.q1[item.q1] = (stats.q1[item.q1] || 0) + 1;
            // Stats Q2
            stats.q2[item.q2] = (stats.q2[item.q2] || 0) + 1;
            // Stats Q3
            stats.q3[item.q3] = (stats.q3[item.q3] || 0) + 1;
        });

        return stats;
    }

    /**
     * Generates a downloadable CSV string of the database records and triggers browser file save.
     */
    static exportToCSV() {
        const signups = this.getSignups();
        if (signups.length === 0) return false;

        const headers = ["Waitlist Position", "Contact Info", "Timestamp", "Q1: Expected Value (Buy To Own)", "Q2: Experience Impact", "Q3: Ecosystem Perception"];
        const rows = signups.map(item => [
            item.waitlistPosition,
            `"${item.emailOrPhone.replace(/"/g, '""')}"`,
            item.timestamp,
            `"${item.q1.replace(/"/g, '""')}"`,
            `"${item.q2.replace(/"/g, '""')}"`,
            `"${item.q3.replace(/"/g, '""')}"`
        ]);

        const csvContent = [headers.join(","), ...rows.map(r => r.join(","))].join("\n");
        const blob = new Blob([csvContent], { type: 'text/csv;charset=utf-8;' });
        const link = document.createElement("a");
        
        if (navigator.msSaveBlob) { // IE 10+
            navigator.msSaveBlob(blob, "waitlist_signups.csv");
            return true;
        }

        const url = URL.createObjectURL(blob);
        link.setAttribute("href", url);
        link.setAttribute("download", `ram_raiders_waitlist_${new Date().toISOString().split('T')[0]}.csv`);
        link.style.visibility = 'hidden';
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
        return true;
    }
}
WaitlistDB.init();
window.WaitlistDB = WaitlistDB; // Export globally for other scripts
